using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// As empresas mais recentemente prospectadas, com porte e melhores compradores. É a
/// pergunta que o time realmente faz — "o que a esteira trouxe hoje?" — e a única forma de
/// julgar se o recorte está mirando o alvo certo, que nenhum número agregado responde. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- ultimos [N]
/// </summary>
public static class ComandoUltimos
{
    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        var quantos = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 12;

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var recentes = await db.LeadScores
            .Include(s => s.Lead)
            .Where(s => s.Lead!.Origem == Lead.OrigemReceita)
            .OrderByDescending(s => s.GeradoEm)
            .Take(quantos)
            .ToListAsync();

        if (recentes.Count == 0) { Console.WriteLine("Nenhuma empresa da Receita pontuada ainda."); return; }

        Console.WriteLine($"=== Últimas {recentes.Count} empresas prospectadas ===\n");

        foreach (var s in recentes)
        {
            var l = s.Lead!;
            Console.WriteLine($"{l.RazaoSocial}");
            Console.WriteLine($"   capital {l.CapitalSocial:C0} | {Util.CnaeCatalogo.Rotulo(l.Cnae)} | {l.Municipio}/{l.Uf}");
            Console.WriteLine($"   porte estimado: {l.PorteEstimado} | score do lead: {s.Score}/100");

            var pares = await db.SinergiasComprador
                .Include(x => x.Comprador)
                .Where(x => x.LeadId == l.Id && x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Take(3)
                .ToListAsync();

            if (pares.Count == 0) Console.WriteLine("   (sem compradores aderentes)");
            foreach (var p in pares)
                Console.WriteLine($"     [{p.Score,3}] {p.Comprador?.Nome}");
            Console.WriteLine();
        }

        var media = recentes.Select(s => s.Lead!.CapitalSocial).DefaultIfEmpty(0).Average();
        Console.WriteLine($"Capital social médio da leva: {media:C0}");
    }
}
