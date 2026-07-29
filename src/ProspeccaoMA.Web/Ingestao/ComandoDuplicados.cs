using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Higiene: encontra leads com a mesma razão social e pares alvo×comprador repetidos
/// (o e-mail de 29/07 mostrou o mesmo par 5 vezes com scores diferentes). Só lê. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- duplicados
/// </summary>
public static class ComandoDuplicados
{
    public static async Task ExecutarAsync(IServiceProvider sp)
    {
        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine("-- Leads com razão social repetida --");
        var leadsDup = await db.Leads
            .GroupBy(l => l.RazaoSocial)
            .Where(g => g.Count() > 1)
            .Select(g => new { Razao = g.Key, Qtd = g.Count() })
            .OrderByDescending(x => x.Qtd)
            .Take(30)
            .ToListAsync();
        foreach (var d in leadsDup)
        {
            var ids = await db.Leads.Where(l => l.RazaoSocial == d.Razao)
                .Select(l => new { l.Id, l.Origem, l.Cnpj }).ToListAsync();
            Console.WriteLine($"  {d.Qtd}x  {d.Razao}");
            foreach (var i in ids)
                Console.WriteLine($"        id={i.Id} cnpj={i.Cnpj ?? "-"} origem={i.Origem}");
        }
        if (leadsDup.Count == 0) Console.WriteLine("  (nenhum)");

        Console.WriteLine("\n-- Pares (LeadId, CompradorId) repetidos --");
        var paresDup = await db.SinergiasComprador
            .GroupBy(s => new { s.LeadId, s.CompradorId })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.LeadId, g.Key.CompradorId, Qtd = g.Count() })
            .OrderByDescending(x => x.Qtd)
            .Take(20)
            .ToListAsync();
        foreach (var p in paresDup)
            Console.WriteLine($"  {p.Qtd}x  leadId={p.LeadId} compradorId={p.CompradorId}");
        if (paresDup.Count == 0) Console.WriteLine("  (nenhum)");

        var totalLeads = await db.Leads.CountAsync();
        var razoesDistintas = await db.Leads.Select(l => l.RazaoSocial).Distinct().CountAsync();
        Console.WriteLine($"\nLeads: {totalLeads} linhas para {razoesDistintas} razões sociais distintas.");
    }
}
