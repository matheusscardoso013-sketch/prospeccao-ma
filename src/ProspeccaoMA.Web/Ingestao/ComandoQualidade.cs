using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Auditoria da QUALIDADE do matching (não da execução): distribuição de scores,
/// concentração por comprador (tese vaga casa com tudo), cobertura dos subscores e
/// amostra de racionais para leitura humana. Só lê. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- qualidade [nome-do-comprador]
/// </summary>
public static class ComandoQualidade
{
    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        if (args.Any(a => a.Equals("--exemplos", StringComparison.OrdinalIgnoreCase)))
        {
            await ExemplosAsync(db);
            return;
        }

        var filtro = args.Length >= 2 ? string.Join(' ', args[1..]) : null;
        if (filtro is not null) { await DetalharCompradorAsync(db, filtro); return; }

        var total = await db.SinergiasComprador.CountAsync();
        Console.WriteLine($"=== Qualidade do matching — {total} pares avaliados ===\n");

        Console.WriteLine("-- Distribuição de scores --");
        var faixas = new (string Rotulo, int Min, int Max)[]
        {
            ("90-100 (excelente)", 90, 100), ("80-89 (quente)", 80, 89),
            ("60-79 (morno)",      60, 79),  ("40-59 (fraco)", 40, 59),
            ("10 (filtro duro)",   10, 10),  ("1-39 (descartado)", 1, 39),
            ("0 (falha da IA)",     0, 0)
        };
        foreach (var f in faixas)
        {
            var n = await db.SinergiasComprador.CountAsync(s => s.Score >= f.Min && s.Score <= f.Max);
            var pct = total == 0 ? 0 : 100.0 * n / total;
            Console.WriteLine($"  {f.Rotulo,-22} {n,5}  {new string('#', (int)Math.Round(pct / 2))} {pct:0.#}%");
        }

        Console.WriteLine("\n-- Desempenho por modelo da rotação --");
        var porModelo = await db.SinergiasComprador
            .Where(s => s.Score > 0 && s.ModeloIA != null)
            .Select(s => new { s.ModeloIA, s.Score, Tam = s.Racional.Length, TemSub = s.ScoreSetor != null })
            .ToListAsync();
        if (porModelo.Count == 0) Console.WriteLine("  (nenhum par carimbado ainda)");
        else
        {
            var mediaGeral = porModelo.Average(x => x.Score);
            Console.WriteLine($"  {"modelo",-32} {"pares",5} {"score",6} {"quentes",8} {"racional",9} {"c/ rubrica",11}");
            foreach (var g in porModelo.GroupBy(x => x.ModeloIA!).OrderByDescending(g => g.Count()))
            {
                var media = g.Average(x => x.Score);
                var alerta = Math.Abs(media - mediaGeral) > 10 || g.Average(x => x.Tam) < 90
                          || 100.0 * g.Count(x => x.TemSub) / g.Count() < 80;
                Console.WriteLine($"  {(alerta ? "!" : " ")}{g.Key,-31} {g.Count(),5} {media,6:0.0} " +
                                  $"{100.0 * g.Count(x => x.Score >= 80) / g.Count(),7:0.#}% {g.Average(x => x.Tam),9:0} " +
                                  $"{100.0 * g.Count(x => x.TemSub) / g.Count(),10:0}%");
            }
            Console.WriteLine($"  (média geral {mediaGeral:0.0} — '!' marca quem foge >10 pontos, escreve <90 chars ou ignora a rubrica)");
        }

        Console.WriteLine("\n-- Subscores preenchidos (rubrica da Onda 1) --");
        var comSub = await db.SinergiasComprador.CountAsync(s => s.ScoreSetor != null);
        Console.WriteLine($"  {comSub}/{total} pares com breakdown setor/porte/modelo/geo");

        Console.WriteLine("\n-- Concentração: compradores que mais aparecem nos QUENTES (>=80) --");
        Console.WriteLine("   (um comprador em muitos quentes = tese genérica casando com tudo)");
        var quentes = await db.SinergiasComprador
            .Where(s => s.Score >= 80)
            .Include(s => s.Comprador)
            .Select(s => new { s.CompradorId, Nome = s.Comprador!.Nome, TeseLen = s.Comprador.Tese.Length,
                               TemCriterios = s.Comprador.CriteriosExtraidosEm != null })
            .ToListAsync();

        foreach (var g in quentes.GroupBy(q => q.CompradorId).OrderByDescending(g => g.Count()).Take(12))
        {
            var p = g.First();
            var pct = quentes.Count == 0 ? 0 : 100.0 * g.Count() / quentes.Count;
            Console.WriteLine($"  {g.Count(),3}x ({pct,4:0.#}% dos quentes)  {p.Nome}" +
                              $"   [tese {p.TeseLen} chars, critérios: {(p.TemCriterios ? "sim" : "NÃO")}]");
        }

        Console.WriteLine("\n-- Alvos que aparecem em muitos quentes (empresa 'coringa') --");
        var porLead = await db.SinergiasComprador
            .Where(s => s.Score >= 80)
            .Include(s => s.Lead)
            .Select(s => new { s.LeadId, Nome = s.Lead!.RazaoSocial })
            .ToListAsync();
        foreach (var g in porLead.GroupBy(x => x.LeadId).OrderByDescending(g => g.Count()).Take(8))
            Console.WriteLine($"  {g.Count(),3}x  {g.First().Nome}");

        Console.WriteLine("\n-- Os pares com score 0: o que aconteceu? --");
        var zeros = await db.SinergiasComprador
            .Where(s => s.Score == 0)
            .GroupBy(s => s.Racional)
            .Select(g => new { Motivo = g.Key, Qtd = g.Count() })
            .OrderByDescending(x => x.Qtd)
            .Take(6).ToListAsync();
        foreach (var z in zeros)
            Console.WriteLine($"  {z.Qtd,5}x  \"{Curto(z.Motivo, 110)}\"");

        var zerosNovos = await db.SinergiasComprador.CountAsync(s => s.Score == 0 && s.Status == StatusSinergia.Novo);
        var novosTotal = await db.SinergiasComprador.CountAsync(s => s.Status == StatusSinergia.Novo);
        Console.WriteLine($"\n  Desses, {zerosNovos} estão como 'Novo' na Mesa — de {novosTotal} linhas totais " +
                          $"({(novosTotal == 0 ? 0 : 100.0 * zerosNovos / novosTotal):0.#}% do que o time vê é par NÃO AVALIADO).");

        var maisAntigo = await db.SinergiasComprador.Where(s => s.Score == 0).MinAsync(s => (DateTime?)s.GeradoEm);
        Console.WriteLine($"  Falha mais antiga na fila: {(maisAntigo is null ? "-" : Util.Fuso.Brasil(maisAntigo.Value).ToString("dd/MM/yyyy"))}");

        Console.WriteLine("\n-- Amostra dos 8 maiores scores (ler o racional com olho crítico) --");
        var amostra = await db.SinergiasComprador
            .Include(s => s.Lead).Include(s => s.Comprador)
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.GeradoEm)
            .Take(8).ToListAsync();
        foreach (var s in amostra)
        {
            Console.WriteLine($"\n  [{s.Score}] {s.Lead?.RazaoSocial}  ×  {s.Comprador?.Nome}");
            Console.WriteLine($"      CNAE {s.Lead?.Cnae} · {s.Lead?.Municipio}/{s.Lead?.Uf} · capital {s.Lead?.CapitalSocial:C0}");
            if (s.ScoreSetor != null)
                Console.WriteLine($"      setor {s.ScoreSetor}/40 · porte {s.ScorePorte}/25 · modelo {s.ScoreModelo}/20 · geo {s.ScoreGeo}/15");
            Console.WriteLine($"      {Curto(s.Racional, 240)}");
        }
    }

    /// <summary>
    /// Evidência de que o motor DISCRIMINA, não só aprova. Um avaliador que só dá nota alta
    /// não está avaliando — o que prova competência é recusar com motivo específico e dar
    /// notas diferentes para o mesmo alvo contra compradores diferentes.
    /// </summary>
    private static async Task ExemplosAsync(AppDbContext db)
    {
        Console.WriteLine("=== RECUSAS BEM FUNDAMENTADAS (score 20-49) ===\n");
        var recusas = await db.SinergiasComprador
            .Include(s => s.Lead).Include(s => s.Comprador)
            .Where(s => s.Score >= 20 && s.Score <= 49 && s.Racional.Length > 120)
            .OrderByDescending(s => s.GeradoEm)
            .Take(8).ToListAsync();
        foreach (var s in recusas)
        {
            Console.WriteLine($"[{s.Score}] {s.Lead?.RazaoSocial}  x  {s.Comprador?.Nome}");
            if (s.ScoreSetor != null)
                Console.WriteLine($"      setor {s.ScoreSetor}/40 | porte {s.ScorePorte}/25 | modelo {s.ScoreModelo}/20 | geo {s.ScoreGeo}/15");
            Console.WriteLine($"      {Curto(s.Racional, 420)}\n");
        }

        Console.WriteLine("\n=== MESMO ALVO, COMPRADORES DIFERENTES (o motor separa?) ===\n");
        var comMuitos = await db.SinergiasComprador
            .Where(s => s.Score > 0)
            .GroupBy(s => s.LeadId)
            .Where(g => g.Count() >= 4)
            .Select(g => new { LeadId = g.Key, Amplitude = g.Max(x => x.Score) - g.Min(x => x.Score) })
            .OrderByDescending(x => x.Amplitude)
            .Take(3).ToListAsync();

        foreach (var c in comMuitos)
        {
            var pares = await db.SinergiasComprador
                .Include(s => s.Lead).Include(s => s.Comprador)
                .Where(s => s.LeadId == c.LeadId && s.Score > 0)
                .OrderByDescending(s => s.Score).ToListAsync();
            Console.WriteLine($"--- {pares[0].Lead?.RazaoSocial} (amplitude {c.Amplitude} pontos) ---");
            foreach (var s in pares)
                Console.WriteLine($"  [{s.Score,3}] {s.Comprador?.Nome}\n        {Curto(s.Racional, 220)}");
            Console.WriteLine();
        }

        Console.WriteLine("\n=== ELIMINADOS POR ARITMETICA, SEM GASTAR IA (score 10) ===\n");
        var duros = await db.SinergiasComprador
            .Include(s => s.Lead).Include(s => s.Comprador)
            .Where(s => s.Score == 10)
            .Take(5).ToListAsync();
        foreach (var s in duros)
            Console.WriteLine($"  {s.Lead?.RazaoSocial} x {s.Comprador?.Nome}\n      {Curto(s.Racional, 260)}\n");
    }

    private static async Task DetalharCompradorAsync(AppDbContext db, string filtro)
    {
        var c = await db.Compradores.FirstOrDefaultAsync(x => x.Nome.Contains(filtro));
        if (c is null) { Console.WriteLine($"Comprador não encontrado: {filtro}"); return; }

        Console.WriteLine($"=== {c.Nome} ===");
        Console.WriteLine($"Tipo: {c.TipoEmpresa ?? "-"} | Responsável: {c.Responsavel ?? "-"}");
        Console.WriteLine($"\nTESE ({c.Tese.Length} chars):\n{c.Tese}\n");
        Console.WriteLine($"Critérios extraídos: {(c.CriteriosExtraidosEm is null ? "NÃO" : c.CriteriosExtraidosEm.ToString())} | validados: {c.CriteriosValidados}");
        Console.WriteLine($"  faturamento {c.FaturamentoMinAlvo:C0} a {c.FaturamentoMaxAlvo:C0} | margem min {c.MargemEbitdaMinima}");
        Console.WriteLine($"  operação: {c.TipoOperacao ?? "-"} | geografia: {c.GeografiaAlvo ?? "-"}");
        Console.WriteLine($"  modelo: {c.ModeloNegocioAlvo ?? "-"}");
        Console.WriteLine($"  exclusões: {c.Exclusoes ?? "-"}");

        var pares = await db.SinergiasComprador
            .Where(s => s.CompradorId == c.Id)
            .Include(s => s.Lead)
            .OrderByDescending(s => s.Score)
            .Take(15).ToListAsync();
        Console.WriteLine($"\nTop {pares.Count} pares deste comprador:");
        foreach (var s in pares)
            Console.WriteLine($"  [{s.Score,3}] {s.Lead?.RazaoSocial} (CNAE {s.Lead?.Cnae})\n        {Curto(s.Racional, 200)}");
    }

    private static string Curto(string? s, int n)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length > n ? s[..n] + "…" : s);
}
