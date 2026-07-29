using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Colapsa os leads da Receita para UMA linha por empresa (CNPJ raiz), corrigindo a
/// importação que trouxe cada ESTABELECIMENTO (matriz + filiais) como se fosse uma
/// empresa diferente. Mantém a MATRIZ (ordem 0001); sem matriz no recorte, mantém a
/// filial com mais trabalho de IA já feito. Alvos curados (Cnpj nulo) não são tocados.
/// Dry-run por padrão; `--gravar` aplica (o cascade leva scores e sinergias das linhas
/// removidas). Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- deduplicar [--gravar]
/// </summary>
public static class ComandoDeduplicar
{
    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var leads = await db.Leads
            .Where(l => l.Cnpj != null && l.Cnpj.Length == 14)
            .Select(l => new { l.Id, l.Cnpj, l.RazaoSocial, l.Municipio, l.CapitalSocial })
            .ToListAsync();

        // Quanto trabalho de IA cada linha carrega (pesa na escolha quando não há matriz).
        var sinergiasPorLead = await db.SinergiasComprador
            .GroupBy(s => s.LeadId)
            .Select(g => new { LeadId = g.Key, Qtd = g.Count() })
            .ToDictionaryAsync(x => x.LeadId, x => x.Qtd);

        var grupos = leads.GroupBy(l => l.Cnpj![..8]).Where(g => g.Count() > 1).ToList();

        var idsRemover = new List<int>();
        var donoPorRemovido = new Dictionary<int, int>(); // filial removida → linha que fica
        foreach (var g in grupos)
        {
            var matriz = g.FirstOrDefault(l => l.Cnpj!.Substring(8, 4) == "0001");
            var manter = matriz ?? g
                .OrderByDescending(l => sinergiasPorLead.GetValueOrDefault(l.Id))
                .ThenBy(l => l.Id)
                .First();
            foreach (var l in g.Where(l => l.Id != manter.Id))
            {
                idsRemover.Add(l.Id);
                donoPorRemovido[l.Id] = manter.Id;
            }
        }

        // Impacto: o cascade leva junto as sinergias das linhas removidas.
        var sinergiasPerdidas = await db.SinergiasComprador.CountAsync(s => idsRemover.Contains(s.LeadId));
        var quentesPerdidas = await db.SinergiasComprador
            .CountAsync(s => idsRemover.Contains(s.LeadId) && s.Score >= 80);
        var descartesPerdidos = await db.SinergiasComprador
            .CountAsync(s => idsRemover.Contains(s.LeadId) && s.Status == StatusSinergia.Descartado);
        var trabalhadasPerdidas = await db.SinergiasComprador
            .CountAsync(s => idsRemover.Contains(s.LeadId) && s.Status != StatusSinergia.Novo
                                                          && s.Status != StatusSinergia.Descartado);

        var totalLeads = await db.Leads.CountAsync();
        Console.WriteLine($"Leads hoje: {totalLeads}  |  empresas da Receita (CNPJ raiz distinta): {leads.Select(l => l.Cnpj![..8]).Distinct().Count()}");
        Console.WriteLine($"Grupos com filiais: {grupos.Count}  |  linhas a remover: {idsRemover.Count}");
        Console.WriteLine($"Ficaria: {totalLeads - idsRemover.Count} leads.\n");
        Console.WriteLine($"Sinergias nas filiais: {sinergiasPerdidas} ({quentesPerdidas} com score >=80) — MIGRADAS para a linha mantida, não descartadas");
        Console.WriteLine($"  descartes com feedback: {descartesPerdidos} (migram junto)");
        Console.WriteLine($"  pares JÁ TRABALHADOS pelo time (Abordado/Reunião/Negociação): {trabalhadasPerdidas}");

        Console.WriteLine("\nAmostra (10 maiores grupos):");
        foreach (var g in grupos.OrderByDescending(x => x.Count()).Take(10))
        {
            var manterId = g.Select(l => l.Id).Except(idsRemover).FirstOrDefault();
            var m = g.First(l => l.Id == manterId);
            Console.WriteLine($"  {g.Count()}x {m.RazaoSocial} → mantém id={m.Id} cnpj={m.Cnpj} ({m.Municipio})");
        }

        if (!gravar)
        {
            Console.WriteLine("\nDRY-RUN — nada foi alterado. Use --gravar para aplicar.");
            return;
        }

        // Antes de apagar: transfere para a linha que fica o trabalho de IA das filiais
        // (é a MESMA empresa — o score já pago vale). Onde a linha mantida já tem o par,
        // fica o de maior score; o resto morre no cascade.
        var conjuntoRemover = idsRemover.ToHashSet();
        var sinergias = await db.SinergiasComprador
            .Where(s => conjuntoRemover.Contains(s.LeadId))
            .ToListAsync();
        var jaExiste = (await db.SinergiasComprador
                .Where(s => !conjuntoRemover.Contains(s.LeadId))
                .Select(s => new { s.LeadId, s.CompradorId, s.Score, s.Id })
                .ToListAsync())
            .ToDictionary(x => (x.LeadId, x.CompradorId), x => (x.Id, x.Score));

        int migradas = 0, substituidas = 0;
        foreach (var s in sinergias.OrderByDescending(s => s.Score))
        {
            var dono = donoPorRemovido[s.LeadId];
            var chave = (dono, s.CompradorId);
            if (jaExiste.TryGetValue(chave, out var atual))
            {
                if (s.Score <= atual.Score) continue;   // o que fica já é melhor
                db.SinergiasComprador.Remove(                // troca pelo melhor
                    await db.SinergiasComprador.FirstAsync(x => x.Id == atual.Id));
                substituidas++;
            }
            s.LeadId = dono;
            jaExiste[chave] = (s.Id, s.Score);
            migradas++;
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Sinergias preservadas: {migradas} migrada(s) para a linha mantida ({substituidas} substituíram par pior).");

        const int lote = 500;
        var removidos = 0;
        for (var i = 0; i < idsRemover.Count; i += lote)
        {
            var fatia = idsRemover.Skip(i).Take(lote).ToList();
            removidos += await db.Leads.Where(l => fatia.Contains(l.Id)).ExecuteDeleteAsync();
            Console.WriteLine($"  removidos {removidos}/{idsRemover.Count}…");
        }
        Console.WriteLine($"\nPronto: {removidos} linha(s) de filial removida(s). Leads agora: {await db.Leads.CountAsync()}.");
    }
}
