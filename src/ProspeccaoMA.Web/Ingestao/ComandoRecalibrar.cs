using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Matching;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Re-pontua os pares "quentes" que nasceram de tese RASA, aplicando a calibração de 30/07
/// (teto 65 quando só dá para julgar o setor).
///
/// Por que é preciso: a calibração vale para avaliações NOVAS. Os pares já pontuados
/// continuavam com a nota inflada — consertamos a régua, mas quem já tinha sido medido
/// seguia com a medida velha. Sem isso, o time continuaria vendo como "quente" o que a
/// régua nova classificaria como morno.
///
/// Escopo deliberadamente estreito (só score >= 80 de tese curta) porque cada par custa uma
/// chamada de IA do orçamento gratuito — e são justamente esses que enganam a Mesa. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- recalibrar [--max N] [--gravar]
/// </summary>
public static class ComandoRecalibrar
{
    /// <summary>Abaixo disso a tese não sustenta julgar porte/modelo/geografia — é o caso
    /// que a medição mostrou inflado (score médio 8,3 pontos acima das teses detalhadas).</summary>
    private const int TeseRasaAte = 100;

    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));
        var max = 40;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--max", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var m)) max = m;

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ia = escopo.ServiceProvider.GetRequiredService<IClassificadorIA>();

        var alvos = await db.SinergiasComprador
            .Include(s => s.Lead).Include(s => s.Comprador)
            .Where(s => s.Score >= 80 && s.Comprador!.Tese.Length < TeseRasaAte)
            .OrderByDescending(s => s.Score)
            .Take(max)
            .ToListAsync();

        Console.WriteLine($"Pares quentes vindos de tese rasa (<{TeseRasaAte} chars): {alvos.Count} | gravar: {gravar}\n");
        foreach (var s in alvos)
            Console.WriteLine($"  [{s.Score}] {s.Lead?.RazaoSocial} x {s.Comprador?.Nome} (tese {s.Comprador?.Tese.Length} chars)");

        if (!gravar)
        {
            Console.WriteLine("\nDRY-RUN — nada alterado. Use --gravar para re-pontuar.");
            return;
        }

        Console.WriteLine("\nRe-pontuando com a régua nova...\n");
        int mudaram = 0, cairam = 0, falhas = 0;
        foreach (var s in alvos)
        {
            if (GeminiClassificador.GeracaoSuspensa)
            {
                Console.WriteLine("ABORTADO: freio de cota acionado — o resto fica para amanhã.");
                break;
            }
            if (s.Lead is null || s.Comprador is null) continue;

            var antes = s.Score;
            var r = await ia.ClassificarSinergiaAsync(s.Lead, s.Comprador);
            if (r.Score == 0) { falhas++; continue; } // IA indisponível: mantém o que havia

            MotorSinergia.AplicarResultado(s, r);
            await db.SaveChangesAsync();

            if (r.Score != antes) mudaram++;
            if (r.Score < 80) cairam++;
            var seta = r.Score < antes ? "v" : r.Score > antes ? "^" : "=";
            Console.WriteLine($"  {antes} {seta} {r.Score}  {s.Lead.RazaoSocial} x {s.Comprador.Nome}");
            if (r.Score < antes) Console.WriteLine($"        {Curto(r.Racional, 200)}");
        }

        Console.WriteLine($"\nResultado: {mudaram} par(es) mudaram de nota, {cairam} deixaram de ser quentes, {falhas} falha(s).");
    }

    private static string Curto(string? s, int n)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length > n ? s[..n] + "…" : s);
}
