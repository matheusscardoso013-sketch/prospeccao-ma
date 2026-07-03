using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Comandos de console da onda "dado rico":
///   estruturar-teses [--max N] [--gravar]   — IA extrai critérios EXPLÍCITOS das teses
///     (só preenche campos vazios; time valida depois na tela de revisão).
///   enriquecer-perfis [--max N] [--gravar]  — visita o SITE OFICIAL de compradores e
///     alvos curados e resume quem são (fonte real; nada inventado).
/// Ambos são idempotentes/retomáveis: pulam quem já foi processado.
/// </summary>
public static partial class ComandoDadoRico
{
    public static async Task EstruturarTesesAsync(IServiceProvider sp, string[] args)
    {
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));
        var max = LerMax(args, 300);

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ia = escopo.ServiceProvider.GetRequiredService<IClassificadorIA>();

        var pendentes = await db.Compradores
            .Where(c => c.Ativo && c.Tese.Length >= 20 && c.CriteriosExtraidosEm == null)
            .OrderBy(c => c.Id)
            .Take(max)
            .ToListAsync();

        Console.WriteLine($"Estruturar teses: {pendentes.Count} comprador(es) pendente(s) (máx {max}) | gravar: {gravar}");

        int ok = 0, semNada = 0, falhas = 0;
        foreach (var c in pendentes)
        {
            if (IA.GeminiClassificador.GeracaoSuspensa)
            {
                Console.WriteLine("ABORTADO: freio de cota acionado (429 em série) — rode de novo quando a cota renovar.");
                break;
            }
            var r = await ia.ExtrairCriteriosTeseAsync(c);
            if (r is null) { falhas++; Console.WriteLine($"  FALHA  {c.Nome}"); continue; }

            var extraidos = new List<string>();
            void Se<T>(T? atual, T? novo, Action aplicar, string rotulo)
            {
                var vazio = atual is null || (atual is string s && string.IsNullOrWhiteSpace(s));
                if (vazio && novo is not null && !(novo is string ns && string.IsNullOrWhiteSpace(ns)))
                {
                    aplicar();
                    extraidos.Add(rotulo);
                }
            }

            Se(c.FaturamentoMinAlvo, r.FaturamentoMin, () => c.FaturamentoMinAlvo = r.FaturamentoMin, $"fat.min {r.FaturamentoMin:C0}");
            Se(c.FaturamentoMaxAlvo, r.FaturamentoMax, () => c.FaturamentoMaxAlvo = r.FaturamentoMax, $"fat.max {r.FaturamentoMax:C0}");
            Se(c.MargemEbitdaMinima, r.MargemEbitdaMinima, () => c.MargemEbitdaMinima = r.MargemEbitdaMinima, $"margem {r.MargemEbitdaMinima}%");
            Se(c.TipoOperacao, r.TipoOperacao, () => c.TipoOperacao = r.TipoOperacao, $"op {r.TipoOperacao}");
            Se(c.GeografiaAlvo, r.Geografia, () => c.GeografiaAlvo = r.Geografia, $"geo {r.Geografia}");
            Se(c.ModeloNegocioAlvo, r.ModeloNegocio, () => c.ModeloNegocioAlvo = r.ModeloNegocio, "modelo");
            Se(c.Exclusoes, r.Exclusoes, () => c.Exclusoes = r.Exclusoes, "exclusões");
            Se(c.Cultura, r.Cultura, () => c.Cultura = r.Cultura, "cultura");

            c.CriteriosExtraidosEm = DateTime.UtcNow;
            c.CriteriosValidados = false;

            if (extraidos.Count > 0) { ok++; Console.WriteLine($"  OK     {c.Nome}: {string.Join("; ", extraidos)}"); }
            else { semNada++; Console.WriteLine($"  VAZIO  {c.Nome}: tese não explicita critérios"); }

            if (gravar) await db.SaveChangesAsync();
        }

        if (!gravar) Console.WriteLine("Dry-run: nada foi gravado (use --gravar).");
        Console.WriteLine($"Resultado: {ok} com critérios, {semNada} sem critérios explícitos, {falhas} falha(s).");
    }

    public static async Task EnriquecerPerfisAsync(IServiceProvider sp, string[] args)
    {
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));
        var max = LerMax(args, 100);

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ia = escopo.ServiceProvider.GetRequiredService<IClassificadorIA>();

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 4 })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; ValoreBot/1.0)");

        int ok = 0, semSite = 0, falhas = 0, feitos = 0;

        // Compradores primeiro (alimentam o matching de todos os alvos), depois os curados.
        var compradores = await db.Compradores
            .Where(c => c.Ativo && c.Site != null && c.Site != "" && c.PerfilSiteEm == null)
            .OrderBy(c => c.Id).ToListAsync();
        var curados = await db.Leads
            .Where(l => l.Origem == Lead.OrigemValore && l.Site != null && l.Site != "" && l.PerfilSiteEm == null)
            .OrderBy(l => l.Id).ToListAsync();

        Console.WriteLine($"Enriquecer perfis: {compradores.Count} comprador(es) + {curados.Count} alvo(s) curado(s) pendentes | máx {max} | gravar: {gravar}");

        foreach (var c in compradores)
        {
            if (feitos >= max) break;
            if (IA.GeminiClassificador.GeracaoSuspensa) { Console.WriteLine("ABORTADO: freio de cota acionado."); break; }
            feitos++;
            var (perfil, motivo) = await PerfilDoSiteAsync(http, ia, c.Nome, c.Site!);
            c.PerfilSiteEm = DateTime.UtcNow;   // marca a tentativa (não refaz site quebrado a cada lote)
            c.PerfilSite = perfil;
            if (perfil is not null) { ok++; Console.WriteLine($"  OK     [comprador] {c.Nome}"); }
            else { falhas++; Console.WriteLine($"  SEM    [comprador] {c.Nome}: {motivo}"); }
            if (gravar) await db.SaveChangesAsync();
        }

        foreach (var l in curados)
        {
            if (feitos >= max) break;
            if (IA.GeminiClassificador.GeracaoSuspensa) { Console.WriteLine("ABORTADO: freio de cota acionado."); break; }
            feitos++;
            var (perfil, motivo) = await PerfilDoSiteAsync(http, ia, l.RazaoSocial, l.Site!);
            l.PerfilSiteEm = DateTime.UtcNow;
            l.PerfilSite = perfil;
            if (perfil is not null) { ok++; Console.WriteLine($"  OK     [alvo] {l.RazaoSocial}"); }
            else { falhas++; Console.WriteLine($"  SEM    [alvo] {l.RazaoSocial}: {motivo}"); }
            if (gravar) await db.SaveChangesAsync();
        }

        if (!gravar) Console.WriteLine("Dry-run: nada foi gravado (use --gravar).");
        Console.WriteLine($"Resultado: {ok} perfil(is) gerado(s), {falhas} sem perfil (site fora/insuficiente), {semSite} sem site.");
    }

    /// <summary>Gera/atualiza o embedding da tese de cada comprador (triagem vetorial).
    /// Retomável: só processa quem não tem vetor ou cuja tese mudou (hash diferente).</summary>
    public static async Task GerarEmbeddingsAsync(IServiceProvider sp, string[] args)
    {
        var max = LerMax(args, 300);

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ia = escopo.ServiceProvider.GetRequiredService<IClassificadorIA>();

        var candidatos = (await db.Compradores
                .Where(c => c.Ativo && c.Tese.Length >= 20)
                .OrderBy(c => c.Id)
                .ToListAsync())
            .Where(c => c.TeseEmbedding == null || c.TeseEmbeddingHash != Matching.MotorSinergia.HashTese(c))
            .Take(max)
            .ToList();

        Console.WriteLine($"Gerar embeddings: {candidatos.Count} tese(s) pendente(s)/desatualizada(s) (máx {max})");

        int ok = 0, falhas = 0;
        foreach (var c in candidatos)
        {
            try
            {
                var v = await ia.GerarEmbeddingAsync(Matching.MotorSinergia.TextoTese(c));
                if (v is null) { falhas++; Console.WriteLine($"  FALHA  {c.Nome}"); continue; }

                c.TeseEmbedding = System.Text.Json.JsonSerializer.Serialize(v);
                c.TeseEmbeddingHash = Matching.MotorSinergia.HashTese(c);
                await db.SaveChangesAsync();
                ok++;
                if (ok % 25 == 0) Console.WriteLine($"  ... {ok} gerado(s)");
            }
            catch (Exception ex)
            {
                // Soluço transitório (rede/Neon) não derruba o lote — o item fica p/ a próxima rodada.
                falhas++;
                Console.WriteLine($"  ERRO   {c.Nome}: {ex.GetType().Name}");
            }
        }
        Console.WriteLine($"Resultado: {ok} embedding(s) gerado(s), {falhas} falha(s).");
    }

    private static async Task<(string? Perfil, string Motivo)> PerfilDoSiteAsync(
        HttpClient http, IClassificadorIA ia, string nome, string site)
    {
        string texto;
        try
        {
            var url = site.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? site : $"https://{site}";
            var html = await http.GetStringAsync(url);
            texto = ExtrairTextoVisivel(html);
            if (texto.Length < 200) return (null, "texto do site insuficiente");
        }
        catch (Exception ex)
        {
            return (null, $"site inacessível ({ex.GetType().Name})");
        }

        var resumo = await ia.ResumirPerfilSiteAsync(nome, texto);
        return resumo is null ? (null, "IA não conseguiu resumir") : (resumo, "ok");
    }

    /// <summary>Reduz o HTML da home ao texto visível (sem scripts/estilos/tags).</summary>
    private static string ExtrairTextoVisivel(string html)
    {
        var t = RxScripts().Replace(html, " ");
        t = RxTags().Replace(t, " ");
        t = System.Net.WebUtility.HtmlDecode(t);
        t = RxEspacos().Replace(t, " ").Trim();
        return t.Length > 6000 ? t[..6000] : t;
    }

    private static int LerMax(string[] args, int padrao)
    {
        var i = Array.FindIndex(args, a => a.Equals("--max", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0 ? n : padrao;
    }

    [GeneratedRegex(@"<(script|style|noscript|svg|head)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RxScripts();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex RxTags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex RxEspacos();
}
