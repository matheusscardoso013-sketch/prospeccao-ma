using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Encontra a MESMA empresa cadastrada dos dois lados da mesa — como alvo e como compradora.
/// Descoberto em 30/07 com o par "Magazord × Magazord", score 90: não é erro do motor (ele
/// avaliou corretamente que a empresa combina consigo mesma), é dado espelhado na origem.
/// O prejuízo é real: ocupa vaga de oportunidade quente e gasta cota de IA.
///
/// Comparação por nome normalizado (sem acento, sem pontuação, sem sufixo societário), que é
/// o que dá para fazer: compradores não têm CNPJ na base. Só lê — a decisão de excluir é do
/// time, porque pode haver homônimos legítimos. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- espelhos
/// </summary>
public static class ComandoEspelhos
{
    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var leads = await db.Leads.Select(l => new { l.Id, l.RazaoSocial, l.Origem, l.Cnpj }).ToListAsync();
        var compradores = await db.Compradores.Where(c => c.Ativo)
            .Select(c => new { c.Id, c.Nome, c.RazaoSocial }).ToListAsync();

        // Índice dos compradores por nome normalizado (nome e razão social, quando houver).
        var porChave = new Dictionary<string, List<(int Id, string Nome)>>();
        foreach (var c in compradores)
        foreach (var bruto in new[] { c.Nome, c.RazaoSocial })
        {
            var k = Normalizar(bruto);
            if (k.Length < 4) continue;
            if (!porChave.TryGetValue(k, out var lista)) porChave[k] = lista = new();
            if (!lista.Any(x => x.Id == c.Id)) lista.Add((c.Id, c.Nome));
        }

        var achados = new List<(int LeadId, string Lead, int CompId, string Comp, string Origem, string? Cnpj)>();
        foreach (var l in leads)
        {
            var k = Normalizar(l.RazaoSocial);
            if (k.Length < 4 || !porChave.TryGetValue(k, out var casados)) continue;
            foreach (var c in casados)
                achados.Add((l.Id, l.RazaoSocial, c.Id, c.Nome, l.Origem, l.Cnpj));
        }

        Console.WriteLine($"=== Empresas cadastradas dos DOIS lados: {achados.Count} ===\n");
        if (achados.Count == 0) { Console.WriteLine("Nenhuma. O par Magazord era caso isolado."); return; }

        foreach (var a in achados)
        {
            var par = await db.SinergiasComprador
                .FirstOrDefaultAsync(s => s.LeadId == a.LeadId && s.CompradorId == a.CompId);
            var marca = par is null ? "sem par gerado" : $"PAR EXISTE — score {par.Score}, status {par.Status}";
            Console.WriteLine($"  {a.Lead}");
            Console.WriteLine($"     alvo id={a.LeadId} ({(a.Cnpj is null ? "sem CNPJ" : a.Cnpj)}, {a.Origem})");
            Console.WriteLine($"     comprador id={a.CompId} \"{a.Comp}\"");
            Console.WriteLine($"     -> {marca}\n");
        }

        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));
        var pares = new List<Models.SinergiaComprador>();
        foreach (var a in achados)
        {
            var p = await db.SinergiasComprador
                .FirstOrDefaultAsync(s => s.LeadId == a.LeadId && s.CompradorId == a.CompId);
            if (p is not null && p.Status != Models.StatusSinergia.Descartado) pares.Add(p);
        }
        Console.WriteLine($"Desses, {pares.Count} geraram par consigo mesmos (falsa oportunidade ocupando vaga na Mesa).");

        if (!gravar)
        {
            Console.WriteLine("\nDRY-RUN — nada alterado. Use --gravar para descartá-los (reversível: é só mudar o status).");
            return;
        }

        // Descartar, não apagar: o registro fica auditável e o time pode reverter se algum
        // for homônimo legítimo em vez da mesma empresa.
        foreach (var p in pares)
        {
            p.Status = Models.StatusSinergia.Descartado;
            p.MotivoDescarte = "Mesma empresa cadastrada como alvo e como compradora";
            p.AtualizadoEm = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"{pares.Count} par(es) descartado(s) com motivo registrado.");
    }

    private static string Normalizar(string? nome) => Util.NomeEmpresa.Chave(nome);
}
