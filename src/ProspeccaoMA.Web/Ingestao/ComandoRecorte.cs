using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Alinha a esteira ao mandato real da Valore (middle market) em uma operação só:
/// atualiza a configuração ativa e remove da base as empresas da Receita fora da faixa.
///
/// Por que foi preciso: até 10/08 a plataforma prospectava EXCLUSIVAMENTE empresas com
/// capital social acima de R$ 200 mi (147 de 147). Três causas empilhadas — a importação
/// filtrava capital >= R$ 50 mi, a configuração pedia R$ 50-500 mi, e a esteira ordena
/// pelas maiores primeiro. O resultado eram matches perfeitos na tese e impossíveis na
/// prática: Takeda, ServiceNow, Progress Rail. A IA vinha avisando em dezenas de racionais
/// ("o porte excede significativamente a faixa da tese") e nós líamos como avaliação
/// individual, não como sinal sistêmico.
///
/// Só remove leads de ORIGEM RECEITA: os alvos curados pela Valore nunca são tocados.
/// Dry-run por padrão. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- recorte --capmin 500000 --capmax 50000000 [--cnaes 62,63] [--gravar]
/// </summary>
public static class ComandoRecorte
{
    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));
        var capMin = Decimal(args, "--capmin");
        var capMax = Decimal(args, "--capmax");
        var cnaes = Texto(args, "--cnaes");

        if (capMin is null || capMax is null)
        {
            Console.WriteLine("Uso: recorte --capmin 500000 --capmax 50000000 [--cnaes 62,63,86] [--gravar]");
            return;
        }

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine($"Faixa alvo: {capMin.Value:C0} a {capMax.Value:C0}\n");

        var configs = await db.Configuracoes.Where(c => c.Ativo).ToListAsync();
        Console.WriteLine("-- Configuração da esteira --");
        foreach (var c in configs)
        {
            Console.WriteLine($"  [{c.Id}] capital {c.CapitalMin:C0} a {c.CapitalMax:C0} | CNAEs: {c.Cnaes}");
            Console.WriteLine($"        vira: capital {capMin.Value:C0} a {capMax.Value:C0}" +
                              (cnaes is null ? " | CNAEs inalterados" : $" | CNAEs: {cnaes}"));
        }

        // --zerar: remove TODA a base da Receita para reimportar limpa. Necessário quando o
        // critério muda de forma que não dá para reavaliar pelo que está gravado — natureza
        // jurídica, por exemplo, não fica no Lead: quem já entrou como empresário individual
        // não teria como ser identificado depois. Curados nunca são tocados.
        var zerar = args.Any(a => a.Equals("--zerar", StringComparison.OrdinalIgnoreCase));
        var foraDaFaixa = zerar
            ? db.Leads.Where(l => l.Origem == Lead.OrigemReceita)
            : db.Leads.Where(l => l.Origem == Lead.OrigemReceita
                && (l.CapitalSocial < capMin.Value || l.CapitalSocial > capMax.Value));
        if (zerar) Console.WriteLine("  MODO ZERAR: toda a base da Receita sai (reimportar em seguida).\n");

        var quantos = await foraDaFaixa.CountAsync();
        var idsFora = await foraDaFaixa.Select(l => l.Id).ToListAsync();
        var paresPerdidos = await db.SinergiasComprador.CountAsync(s => idsFora.Contains(s.LeadId));
        var quentesPerdidos = await db.SinergiasComprador
            .CountAsync(s => idsFora.Contains(s.LeadId) && s.Score >= 80);
        var trabalhados = await db.SinergiasComprador
            .CountAsync(s => idsFora.Contains(s.LeadId)
                && s.Status != StatusSinergia.Novo && s.Status != StatusSinergia.Descartado);

        var dentro = await db.Leads.CountAsync(l => l.Origem == Lead.OrigemReceita
            && l.CapitalSocial >= capMin.Value && l.CapitalSocial <= capMax.Value);
        var curados = await db.Leads.CountAsync(l => l.Origem == Lead.OrigemValore);

        Console.WriteLine($"\n-- Base --");
        Console.WriteLine($"  Receita DENTRO da faixa (ficam): {dentro}");
        Console.WriteLine($"  Receita FORA da faixa (saem):    {quantos}");
        Console.WriteLine($"  Alvos curados da Valore:         {curados}  (nunca tocados)");
        Console.WriteLine($"\n  Sinergias que somem junto: {paresPerdidos} ({quentesPerdidos} com score >=80)");
        Console.WriteLine($"  Pares JÁ TRABALHADOS pelo time que se perderiam: {trabalhados}");

        if (trabalhados > 0)
            Console.WriteLine("  *** ATENÇÃO: há trabalho humano nesses pares. Reveja antes de gravar. ***");

        if (!gravar)
        {
            Console.WriteLine("\nDRY-RUN — nada alterado. Use --gravar para aplicar.");
            return;
        }

        foreach (var c in configs)
        {
            c.CapitalMin = capMin;
            c.CapitalMax = capMax;
            if (cnaes is not null) c.Cnaes = cnaes;
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"\nConfiguração atualizada ({configs.Count}).");

        const int lote = 500;
        var removidos = 0;
        for (var i = 0; i < idsFora.Count; i += lote)
        {
            var fatia = idsFora.Skip(i).Take(lote).ToList();
            removidos += await db.Leads.Where(l => fatia.Contains(l.Id)).ExecuteDeleteAsync();
            Console.WriteLine($"  removidos {removidos}/{idsFora.Count}…");
        }

        Console.WriteLine($"\nPronto: {removidos} empresa(s) fora da faixa removida(s). " +
                          $"Base agora: {await db.Leads.CountAsync()} leads.");
    }

    private static decimal? Decimal(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i < 0 || i + 1 >= args.Length ? null
            : decimal.TryParse(args[i + 1], System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? Texto(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i < 0 || i + 1 >= args.Length ? null : args[i + 1];
    }
}
