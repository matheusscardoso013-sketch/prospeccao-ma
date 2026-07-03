using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.IA;

public class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Modelo { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>Modelo tentado automaticamente quando o principal esgota as tentativas
    /// (o free tier do flash satura com frequência; o lite tem limites maiores).</summary>
    public string ModeloFallback { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>Modelo forte do segundo estágio (re-pontua finalistas). Vazio = desligado.</summary>
    public string ModeloPreciso { get; set; } = "gemini-2.5-flash";

    /// <summary>Modelo de embeddings (cota separada da geração no free tier).</summary>
    public string ModeloEmbedding { get; set; } = "gemini-embedding-001";
}

/// <summary>
/// Qualificação via Google Gemini (free tier). O prompt contém APENAS dados reais do
/// candidato e instrui explicitamente a não inventar nada. A resposta é exigida em JSON
/// estrito {"score":0-100,"racional":"..."} e o parsing é protegido por try/catch.
/// </summary>
public partial class GeminiClassificador : IClassificadorIA
{
    private readonly HttpClient _http;
    private readonly ILogger<GeminiClassificador> _log;
    private readonly GeminiOptions _opt;

    private static readonly JsonSerializerOptions JsonInsensitive = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex BlocoJson();

    public GeminiClassificador(HttpClient http, ILogger<GeminiClassificador> log, IConfiguration cfg)
    {
        _http = http;
        _log = log;
        _opt = new GeminiOptions();
        cfg.GetSection("Gemini").Bind(_opt);
    }

    public async Task<ResultadoClassificacao> ClassificarAsync(
        Lead lead, ConfiguracaoProspeccao config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
        {
            _log.LogError("Gemini:ApiKey não configurada (use user-secrets). Lead {Cnpj} não pontuado.", lead.Cnpj);
            return new ResultadoClassificacao(0, "IA não configurada: defina Gemini:ApiKey para qualificar este lead.");
        }

        return await ChamarAsync(MontarPrompt(lead, config), lead.Cnpj ?? lead.RazaoSocial, ct);
    }

    public async Task<ResultadoClassificacao> ClassificarSinergiaAsync(
        Lead lead, Comprador comprador, bool preciso = false, string? feedback = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            return new ResultadoClassificacao(0, "IA não configurada: defina Gemini:ApiKey.");

        var prompt = MontarPromptSinergia(lead, comprador, feedback);
        var idLog = $"{lead.Cnpj}~{comprador.Nome}";

        // Segundo estágio: o modelo forte re-pontua o finalista; se ele falhar (cota),
        // devolvemos falha e o chamador mantém o resultado do primeiro estágio.
        if (preciso && !string.IsNullOrWhiteSpace(_opt.ModeloPreciso))
        {
            var texto = await TentarModeloAsync(_opt.ModeloPreciso, prompt, $"preciso~{idLog}", ct);
            return texto is null
                ? new ResultadoClassificacao(0, "IA indisponível no momento (limite de uso); tente novamente.")
                : ParsearResultado(texto, idLog);
        }

        return await ChamarAsync(prompt, idLog, ct);
    }

    // Espaçamento próprio dos embeddings (cota/limite separados da geração; muito mais folgados).
    private static readonly SemaphoreSlim _portaEmbed = new(1, 1);
    private static DateTime _ultimaEmbed = DateTime.MinValue;
    private static readonly TimeSpan IntervaloEmbed = TimeSpan.FromMilliseconds(700);

    public async Task<float[]?> GerarEmbeddingAsync(string texto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey) || string.IsNullOrWhiteSpace(texto)) return null;

        var corpo = new
        {
            model = $"models/{_opt.ModeloEmbedding}",
            content = new { parts = new[] { new { text = texto.Length > 7000 ? texto[..7000] : texto } } },
            outputDimensionality = 768 // Matryoshka: 768 dims bastam p/ triagem e pesam 4x menos
        };
        var url = $"v1beta/models/{_opt.ModeloEmbedding}:embedContent?key={_opt.ApiKey}";

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            try
            {
                await _portaEmbed.WaitAsync(ct);
                try
                {
                    var decorrido = DateTime.UtcNow - _ultimaEmbed;
                    if (decorrido < IntervaloEmbed) await Task.Delay(IntervaloEmbed - decorrido, ct);
                    _ultimaEmbed = DateTime.UtcNow;
                }
                finally { _portaEmbed.Release(); }

                using var req = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, req, ct);
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    if (tentativa < 3) { await Task.Delay(TimeSpan.FromSeconds(3 * tentativa), ct); continue; }
                    _log.LogWarning("Embedding rate limit/erro {Status}", (int)resp.StatusCode);
                    return null;
                }
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var valores = doc.RootElement.GetProperty("embedding").GetProperty("values");
                var v = new float[valores.GetArrayLength()];
                var i = 0;
                foreach (var e in valores.EnumerateArray()) v[i++] = e.GetSingle();
                return v;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                if (tentativa >= 3) { _log.LogWarning(ex, "Falha ao gerar embedding"); return null; }
                await Task.Delay(TimeSpan.FromSeconds(2 * tentativa), ct);
            }
        }
        return null;
    }

    // Espaçamento mínimo entre chamadas: o free tier do gemini-2.5-flash permite ~10 req/min,
    // então ~6,5s/chamada (~9/min) evita o 429 por RPM (1,5s causava rajada de ~40/min).
    private static readonly SemaphoreSlim _porta = new(1, 1);
    private static DateTime _ultima = DateTime.MinValue;
    private static readonly TimeSpan IntervaloMin = TimeSpan.FromMilliseconds(6500);
    private const int MaxTentativas = 4;

    /// <summary>Chamada genérica ao Gemini (score+racional) com parsing defensivo. Nunca lança.</summary>
    private async Task<ResultadoClassificacao> ChamarAsync(string prompt, string idLog, CancellationToken ct)
    {
        var texto = await ChamarTextoAsync(prompt, idLog, ct);
        if (texto is null)
            return new ResultadoClassificacao(0, "IA indisponível no momento (limite de uso); tente novamente.");
        return ParsearResultado(texto, idLog);
    }

    // Circuit breaker do modelo principal: se ele falhar 2x seguidas (saturação do free
    // tier), vai direto ao fallback por 10 min — evita desperdiçar ~30s de retries por
    // chamada numa rodada longa (60 alvos × 13 chamadas viraria horas perdidas).
    private static int _falhasPrimarioSeguidas;
    private static DateTime _primarioSuspensoAte = DateTime.MinValue;

    // FREIO GLOBAL DE COTA: quando a cota DIÁRIA esgota, cada chamada ainda gastava 4
    // tentativas (retry storm) — a esteira horária sozinha queimava ~960 req/dia em retries
    // e a cota nunca se recuperava. Após 3 chamadas seguidas terminando em 429, TODA a
    // geração fica suspensa por 45 min (retorna null na hora); qualquer sucesso rearma.
    private static int _finais429Seguidos;
    private static DateTime _geracaoSuspensaAte = DateTime.MinValue;

    /// <summary>Geração suspensa pelo freio global de cota (visível p/ lotes abortarem cedo).</summary>
    public static bool GeracaoSuspensa => DateTime.UtcNow < _geracaoSuspensaAte;

    /// <summary>Chamada bruta ao Gemini: tenta o modelo principal e, se ele esgotar as
    /// tentativas (cota/saturação do free tier), cai automaticamente para o ModeloFallback.</summary>
    private async Task<string?> ChamarTextoAsync(string prompt, string idLog, CancellationToken ct)
    {
        if (GeracaoSuspensa)
        {
            _log.LogDebug("Geração suspensa pelo freio de cota — chamada pulada ({Id})", idLog);
            return null;
        }

        var temFallback = !string.Equals(_opt.Modelo, _opt.ModeloFallback, StringComparison.OrdinalIgnoreCase);

        string? texto = null;
        if (!temFallback || DateTime.UtcNow >= _primarioSuspensoAte)
        {
            texto = await TentarModeloAsync(_opt.Modelo, prompt, idLog, ct);
            if (texto is not null)
            {
                _falhasPrimarioSeguidas = 0;
            }
            else if (temFallback && Interlocked.Increment(ref _falhasPrimarioSeguidas) >= 2)
            {
                _primarioSuspensoAte = DateTime.UtcNow.AddMinutes(10);
                _falhasPrimarioSeguidas = 0;
                _log.LogWarning("Modelo {Modelo} saturado — usando só o fallback {Fallback} pelos próximos 10 min",
                    _opt.Modelo, _opt.ModeloFallback);
            }
        }

        if (texto is null && temFallback)
        {
            _log.LogInformation("Usando fallback {Fallback} ({Id})", _opt.ModeloFallback, idLog);
            texto = await TentarModeloAsync(_opt.ModeloFallback, prompt, idLog, ct);
        }
        return texto;
    }

    /// <summary>Tenta um modelo específico com retry/backoff (429/5xx). Null se esgotar
    /// ou se a operação for cancelada (ex.: tempo-limite do chamador) — nunca lança.</summary>
    private async Task<string?> TentarModeloAsync(string modelo, string prompt, string idLog, CancellationToken ct)
    {
        try { return await TentarModeloInternoAsync(modelo, prompt, idLog, ct); }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Chamada ao Gemini cancelada pelo tempo-limite do chamador ({Id})", idLog);
            return null;
        }
    }

    private async Task<string?> TentarModeloInternoAsync(string modelo, string prompt, string idLog, CancellationToken ct)
    {
        var corpo = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
        };
        var url = $"v1beta/models/{modelo}:generateContent?key={_opt.ApiKey}";

        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            await EspacarAsync(ct);
            try
            {
                using var req = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, req, ct);

                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    if (tentativa < MaxTentativas) { await BackoffAsync(tentativa, ct); continue; }
                    _log.LogWarning("Gemini rate limit/erro {Status} após {N} tentativas ({Id})", (int)resp.StatusCode, tentativa, idLog);
                    if ((int)resp.StatusCode == 429 && Interlocked.Increment(ref _finais429Seguidos) >= 3)
                    {
                        _geracaoSuspensaAte = DateTime.UtcNow.AddMinutes(45);
                        _log.LogWarning("FREIO DE COTA acionado: 3 chamadas seguidas esgotaram em 429 — geração suspensa por 45 min (retries não vão mais canibalizar a cota).");
                    }
                    return null;
                }

                resp.EnsureSuccessStatusCode();
                _finais429Seguidos = 0; // sucesso rearma o freio
                var json = await resp.Content.ReadAsStringAsync(ct);
                return ExtrairTextoDaResposta(json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && tentativa < MaxTentativas)
            {
                _log.LogWarning(ex, "Falha transitória no Gemini (tentativa {N}, {Id})", tentativa, idLog);
                await BackoffAsync(tentativa, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha na chamada ao Gemini ({Id})", idLog);
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Triagem semântica (1 chamada): dado o alvo e a lista de compradores com tese, a IA
    /// devolve os ids dos mais aderentes. Null em falha — o chamador usa o fallback por
    /// palavras-chave. A IA só escolhe dentre os listados (não inventa compradores).
    /// </summary>
    public async Task<List<int>?> SelecionarCompradoresAsync(
        Lead lead, IReadOnlyList<Comprador> compradores, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey) || compradores.Count == 0) return null;

        var texto = await ChamarTextoAsync(MontarPromptTriagem(lead, compradores, max),
            $"triagem~{lead.RazaoSocial}", ct);
        if (string.IsNullOrWhiteSpace(texto)) return null;

        try
        {
            var bruto = texto.Trim();
            var m = BlocoJson().Match(bruto);
            if (m.Success) bruto = m.Value;

            using var doc = JsonDocument.Parse(bruto);
            if (!doc.RootElement.TryGetProperty("ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var validos = compradores.Select(c => c.Id).ToHashSet();
            var ids = arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                .Select(e => e.GetInt32())
                .Where(validos.Contains)
                .Distinct()
                .Take(max)
                .ToList();

            return ids.Count > 0 ? ids : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Triagem da IA fora do formato esperado; usando fallback. Resposta: {Texto}", texto);
            return null;
        }
    }

    private static string MontarPromptTriagem(Lead lead, IReadOnlyList<Comprador> compradores, int max)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A buy-side. Dada UMA empresa-alvo real e a lista de compradores");
        sb.AppendLine($"com suas teses, selecione os até {max} compradores com MAIOR potencial de fit (setor, porte, modelo, geografia).");
        sb.AppendLine("Regras: escolha SOMENTE ids da lista; não invente; prefira aderência de tese a fama do nome.");
        sb.AppendLine("Responda ESTRITAMENTE em JSON: {\"ids\": [<id>, <id>, ...]} — nada além disso.");
        sb.AppendLine();
        sb.AppendLine("## Empresa-alvo (dados reais)");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        if (!string.IsNullOrWhiteSpace(lead.Cnae)) sb.AppendLine($"- Atividade (CNAE oficial): {Util.CnaeCatalogo.ParaPrompt(lead.Cnae)}");
        if (!string.IsNullOrWhiteSpace(lead.Segmento)) sb.AppendLine($"- Segmento: {lead.Segmento}");
        if (!string.IsNullOrWhiteSpace(lead.Uf)) sb.AppendLine($"- UF: {lead.Uf}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        if (lead.CapitalSocial > 0) sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        if (!string.IsNullOrWhiteSpace(lead.Descricao)) sb.AppendLine($"- Resumo: {Resumir(lead.Descricao, 500)}");
        sb.AppendLine();
        sb.AppendLine("## Compradores (id | nome | tese | critérios)");
        foreach (var c in compradores)
        {
            var setor = string.Join("/", new[] { c.TipoEmpresa, c.Segmento }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var extras = new List<string>();
            if (c.FaturamentoMinAlvo is not null || c.FaturamentoMaxAlvo is not null)
                extras.Add($"fat. alvo {c.FaturamentoMinAlvo?.ToString("C0") ?? "até"}–{c.FaturamentoMaxAlvo?.ToString("C0") ?? "s/ teto"}");
            if (!string.IsNullOrWhiteSpace(c.Exclusoes)) extras.Add($"NÃO olha: {Resumir(c.Exclusoes, 60)}");
            var sufixo = extras.Count > 0 ? $" [{string.Join("; ", extras)}]" : "";
            sb.AppendLine($"[{c.Id}] {c.Nome}{(setor.Length > 0 ? $" ({setor})" : "")} — {Resumir(c.Tese, 200)}{sufixo}");
        }
        return sb.ToString();
    }

    /// <summary>Extrai critérios estruturados EXPLÍCITOS da tese (anti-presunção). Null em falha.</summary>
    public async Task<CriteriosTese?> ExtrairCriteriosTeseAsync(Comprador comprador, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey) || string.IsNullOrWhiteSpace(comprador.Tese)) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A. Extraia do texto da TESE DE INVESTIMENTO abaixo os critérios estruturados EXPLÍCITOS.");
        sb.AppendLine("Regras rígidas:");
        sb.AppendLine("- Extraia SOMENTE o que está escrito de forma explícita ou inequívoca no texto. NUNCA presuma ou complete.");
        sb.AppendLine("- O que o texto não disser, devolva null.");
        sb.AppendLine("- Valores monetários em NÚMERO (reais/ano). Ex.: 'R$ 30 a 120 mi de receita' → fat_min 30000000, fat_max 120000000.");
        sb.AppendLine("- margem_min em número percentual (ex.: 'EBITDA acima de 15%' → 15).");
        sb.AppendLine("- tipo_operacao: 'Controle', 'Minoritária', '100%' ou 'Indiferente' (só se explícito).");
        sb.AppendLine("- exclusoes: o que a tese diz que NÃO olham (red flags), texto curto.");
        sb.AppendLine("Responda ESTRITAMENTE em JSON:");
        sb.AppendLine("{\"fat_min\":n|null,\"fat_max\":n|null,\"margem_min\":n|null,\"tipo_operacao\":\"\"|null,\"geografia\":\"\"|null,\"modelo\":\"\"|null,\"exclusoes\":\"\"|null,\"cultura\":\"\"|null}");
        sb.AppendLine();
        sb.AppendLine($"## Comprador: {comprador.Nome}");
        if (!string.IsNullOrWhiteSpace(comprador.FaixaFaturamento)) sb.AppendLine($"## Faixa de faturamento (campo da planilha): {comprador.FaixaFaturamento}");
        sb.AppendLine($"## Tese: {Resumir(comprador.Tese, 2500)}");

        var texto = await ChamarTextoAsync(sb.ToString(), $"criterios~{comprador.Nome}", ct);
        if (string.IsNullOrWhiteSpace(texto)) return null;

        try
        {
            var bruto = texto.Trim();
            var m = BlocoJson().Match(bruto);
            if (m.Success) bruto = m.Value;
            using var doc = JsonDocument.Parse(bruto);
            var r = doc.RootElement;

            return new CriteriosTese(
                LerDecimal(r, "fat_min"), LerDecimal(r, "fat_max"), LerDecimal(r, "margem_min"),
                LerTexto(r, "tipo_operacao"), LerTexto(r, "geografia"), LerTexto(r, "modelo"),
                LerTexto(r, "exclusoes"), LerTexto(r, "cultura"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Extração de critérios fora do formato ({Nome}). Resposta: {Texto}", comprador.Nome, texto);
            return null;
        }
    }

    /// <summary>Resume o perfil da empresa com base APENAS no texto do site. Null se insuficiente.</summary>
    public async Task<string?> ResumirPerfilSiteAsync(string nomeEmpresa, string textoSite, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey) || string.IsNullOrWhiteSpace(textoSite)) return null;

        var sb = new StringBuilder();
        sb.AppendLine("Resuma QUEM É esta empresa com base APENAS no texto abaixo, extraído do site oficial dela.");
        sb.AppendLine("Regras: 2-4 frases objetivas (o que faz, para quem, modelo de negócio, portfólio/aquisições se citados).");
        sb.AppendLine("NUNCA acrescente informação que não esteja no texto. Se o texto não permitir um resumo útil, responda {\"resumo\":null}.");
        sb.AppendLine("Responda ESTRITAMENTE em JSON: {\"resumo\":\"...\"} ou {\"resumo\":null}.");
        sb.AppendLine();
        sb.AppendLine($"## Empresa: {nomeEmpresa}");
        sb.AppendLine($"## Texto do site: {Resumir(textoSite, 4000)}");

        var texto = await ChamarTextoAsync(sb.ToString(), $"perfil~{nomeEmpresa}", ct);
        if (string.IsNullOrWhiteSpace(texto)) return null;

        try
        {
            var bruto = texto.Trim();
            var m = BlocoJson().Match(bruto);
            if (m.Success) bruto = m.Value;
            using var doc = JsonDocument.Parse(bruto);
            var resumo = LerTexto(doc.RootElement, "resumo");
            return string.IsNullOrWhiteSpace(resumo) || resumo.Length < 30 ? null : resumo;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Resumo de perfil fora do formato ({Nome}).", nomeEmpresa);
            return null;
        }
    }

    private static decimal? LerDecimal(JsonElement raiz, string prop)
    {
        if (!raiz.TryGetProperty(prop, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String && decimal.TryParse(e.GetString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static string? LerTexto(JsonElement raiz, string prop)
    {
        if (!raiz.TryGetProperty(prop, out var e) || e.ValueKind != JsonValueKind.String) return null;
        var v = e.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(v) || v is "null" or "N/A" or "n/a" ? null : v;
    }

    private static async Task EspacarAsync(CancellationToken ct)
    {
        await _porta.WaitAsync(ct);
        try
        {
            var decorrido = DateTime.UtcNow - _ultima;
            if (decorrido < IntervaloMin) await Task.Delay(IntervaloMin - decorrido, ct);
            _ultima = DateTime.UtcNow;
        }
        finally { _porta.Release(); }
    }

    private static Task BackoffAsync(int tentativa, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(2 * Math.Pow(2, tentativa)), ct); // 4s,8s,16s

    /// <summary>Prompt buy-side: fit do lead REAL com a tese do comprador. Anti-invenção.</summary>
    private static string MontarPromptSinergia(Lead lead, Comprador comprador, string? feedback = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A buy-side avaliando o fit entre uma empresa-alvo REAL e a TESE de investimento de um comprador.");
        sb.AppendLine("Pontue usando ESTA RUBRICA (subnotas independentes):");
        sb.AppendLine("- setor (0-40): aderência da atividade do alvo aos setores/segmentos da tese.");
        sb.AppendLine("- porte (0-25): compatibilidade do porte/ticket do alvo com a faixa buscada. Se a tese não especifica faixa, máximo 15.");
        sb.AppendLine("- modelo (0-20): fit do modelo de negócio (recorrência, B2B/B2C, serviços vs produto, contratos).");
        sb.AppendLine("- geografia (0-15): fit geográfico. Se a tese não restringe geografia, dê 10.");
        sb.AppendLine("score = setor + porte + modelo + geografia.");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- RED FLAG: se o alvo viola uma exclusão explícita da tese (ex.: 'não olham produto', 'sem muitos PJs'), o score final é no MÁXIMO 20 e o racional cita a violação.");
        sb.AppendLine("- DADOS FALTANTES: não presuma a favor — reduza a subnota correspondente e cite a lacuna no racional.");
        sb.AppendLine("- NÃO invente informações; porte/faturamento do alvo são ESTIMADOS (capital social é proxy).");
        sb.AppendLine("Calibração (use a escala inteira — a maioria dos pares reais fica entre 30 e 70):");
        sb.AppendLine("- ~90 = fit raro: setor exato da tese + porte dentro da faixa + modelo buscado (ex.: SaaS B2B recorrente para consolidador de software na faixa certa).");
        sb.AppendLine("- ~60 = fit parcial digno de análise: setor adjacente OU porte nas bordas da faixa, sem violações (ex.: distribuidora de alimentos para fundo de consumo que prefere marcas próprias).");
        sb.AppendLine("- ~30 = fit fraco: só coincidências genéricas, setor distinto do núcleo da tese (ex.: indústria pesada para tese de saúde; varejo físico para tese de software).");
        sb.AppendLine("- Responda ESTRITAMENTE em JSON:");
        sb.AppendLine("{\"setor\":n,\"porte\":n,\"modelo\":n,\"geografia\":n,\"score\":n,\"racional\":\"<1-3 frases objetivas>\"}");
        sb.AppendLine();
        sb.AppendLine("## Comprador e sua tese");
        sb.AppendLine($"- Nome: {comprador.Nome}");
        if (!string.IsNullOrWhiteSpace(comprador.TipoEmpresa)) sb.AppendLine($"- Tipo: {comprador.TipoEmpresa}");
        if (!string.IsNullOrWhiteSpace(comprador.Segmento)) sb.AppendLine($"- Segmento: {comprador.Segmento}");
        if (!string.IsNullOrWhiteSpace(comprador.Tags)) sb.AppendLine($"- Tags da tese: {comprador.Tags}");
        sb.AppendLine($"- Tese: {Resumir(comprador.Tese, 1500)}");
        if (!string.IsNullOrWhiteSpace(comprador.PerfilSite))
            sb.AppendLine($"- Perfil (do site oficial): {Resumir(comprador.PerfilSite, 600)}");
        sb.AppendLine("### Critérios estruturados do comprador (quando informados, têm prioridade sobre o texto da tese)");
        if (comprador.FaturamentoMinAlvo is not null || comprador.FaturamentoMaxAlvo is not null)
            sb.AppendLine($"- Faixa de faturamento alvo: {(comprador.FaturamentoMinAlvo is null ? "até" : comprador.FaturamentoMinAlvo.Value.ToString("C0"))} a {(comprador.FaturamentoMaxAlvo is null ? "sem teto" : comprador.FaturamentoMaxAlvo.Value.ToString("C0"))} — pontue a subnota 'porte' comparando com o faturamento estimado do alvo.");
        else if (!string.IsNullOrWhiteSpace(comprador.FaixaFaturamento))
            sb.AppendLine($"- Faixa de faturamento alvo (texto): {comprador.FaixaFaturamento}");
        if (comprador.MargemEbitdaMinima is not null)
            sb.AppendLine($"- Margem EBITDA mínima exigida: {comprador.MargemEbitdaMinima}% — alvo abaixo disso perde pontos em 'porte'.");
        if (!string.IsNullOrWhiteSpace(comprador.TipoOperacao))
            sb.AppendLine($"- Tipo de operação buscada: {comprador.TipoOperacao}");
        if (!string.IsNullOrWhiteSpace(comprador.GeografiaAlvo))
            sb.AppendLine($"- Geografia alvo: {comprador.GeografiaAlvo}");
        if (!string.IsNullOrWhiteSpace(comprador.ModeloNegocioAlvo))
            sb.AppendLine($"- Modelo de negócio buscado: {comprador.ModeloNegocioAlvo}");
        if (!string.IsNullOrWhiteSpace(comprador.Exclusoes))
            sb.AppendLine($"- EXCLUSÕES (red flags ELIMINATÓRIAS — score máximo 20 se o alvo violar): {comprador.Exclusoes}");
        if (!string.IsNullOrWhiteSpace(comprador.Cultura))
            sb.AppendLine($"- Cultura/fit desejado: {comprador.Cultura}");
        if (!string.IsNullOrWhiteSpace(feedback))
        {
            sb.AppendLine("### Aprendizado da mesa (alvos que o time JÁ DESCARTOU para este comprador, com o motivo)");
            sb.AppendLine("Considere esses padrões: alvo semelhante aos descartados deve perder pontos na dimensão citada.");
            sb.AppendLine(feedback);
        }
        sb.AppendLine();
        sb.AppendLine($"## Empresa-alvo (dados reais — {lead.Origem})");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        if (!string.IsNullOrWhiteSpace(lead.Cnae))
            sb.AppendLine($"- Atividade (CNAE oficial): {Util.CnaeCatalogo.ParaPrompt(lead.Cnae)}");
        if (!string.IsNullOrWhiteSpace(lead.Uf))
            sb.AppendLine($"- UF/Município: {lead.Uf}/{lead.Municipio}");
        if (!string.IsNullOrWhiteSpace(lead.Segmento))
            sb.AppendLine($"- Segmento: {lead.Segmento}");
        if (lead.CapitalSocial > 0)
            sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        if (!string.IsNullOrWhiteSpace(lead.Situacao))
            sb.AppendLine($"- Situação: {lead.Situacao}");
        if (!string.IsNullOrWhiteSpace(lead.MargemEbitda))
            sb.AppendLine($"- Margem EBITDA (estimada): {lead.MargemEbitda}");
        if (!string.IsNullOrWhiteSpace(lead.ValuationEstimado))
            sb.AppendLine($"- Valuation (estimado): {lead.ValuationEstimado}");
        if (!string.IsNullOrWhiteSpace(lead.ModeloNegocio))
            sb.AppendLine($"- Modelo de negócio: {lead.ModeloNegocio}");
        if (!string.IsNullOrWhiteSpace(lead.Abrangencia))
            sb.AppendLine($"- Abrangência de atuação: {lead.Abrangencia}");
        if (!string.IsNullOrWhiteSpace(lead.Cultura))
            sb.AppendLine($"- Cultura/gestão: {lead.Cultura}");
        if (!string.IsNullOrWhiteSpace(lead.Descricao))
            sb.AppendLine($"- Resumo da empresa: {Resumir(lead.Descricao, 1200)}");
        if (!string.IsNullOrWhiteSpace(lead.PerfilSite))
            sb.AppendLine($"- Perfil (do site oficial): {Resumir(lead.PerfilSite, 600)}");
        sb.AppendLine();
        sb.AppendLine("Avalie o fit (0-100) e escreva um racional curto (1-3 frases).");
        return sb.ToString();
    }

    private static string Resumir(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? "(sem tese registrada)" : (s.Length > max ? s.Substring(0, max) + "…" : s);

    /// <summary>Prompt com SOMENTE dados reais e instrução anti-invenção (spec seção 4).</summary>
    private static string MontarPrompt(Lead lead, ConfiguracaoProspeccao config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A sell-side de uma boutique focada em MIDDLE MARKET.");
        sb.AppendLine("Avalie o potencial desta empresa-alvo REAL como candidata a uma operação de VENDA (sell-side).");
        sb.AppendLine("Pontue usando ESTA RUBRICA (subnotas independentes):");
        sb.AppendLine("- setor (0-50): aderência exata da atividade (CNAE) aos setores do mandato. Atividade genérica/adjacente vale menos.");
        sb.AppendLine("- porte (0-30): posição do capital social DENTRO da faixa do mandato (meio da faixa vale mais que as bordas; gigantes acima da faixa valem pouco).");
        sb.AppendLine("- dados (0-20): completude e qualidade dos dados disponíveis (contato, situação, clareza da atividade).");
        sb.AppendLine("score = setor + porte + dados. Use a escala inteira: candidatos medianos devem ficar em 40-70, não em 90+.");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- NÃO invente informações; porte/faturamento são ESTIMADOS (capital social é proxy).");
        sb.AppendLine("- DADOS FALTANTES: não presuma a favor; reduza a subnota e cite a lacuna.");
        sb.AppendLine("- Responda ESTRITAMENTE em JSON:");
        sb.AppendLine("{\"setor\":n,\"porte\":n,\"dados\":n,\"score\":n,\"racional\":\"<1-3 frases, terminando com a linha 'Setor n/50 · Porte n/30 · Dados n/20'>\"}");
        sb.AppendLine();
        sb.AppendLine("## Mandato (setores de interesse do cliente)");
        sb.AppendLine($"- CNAEs alvo: {config.Cnaes}");
        sb.AppendLine($"- UFs alvo: {config.Ufs}");
        if (config.CapitalMin is not null) sb.AppendLine($"- Capital social mínimo desejado: {config.CapitalMin:C}");
        if (config.CapitalMax is not null) sb.AppendLine($"- Capital social máximo desejado: {config.CapitalMax:C}");
        sb.AppendLine();
        sb.AppendLine("## Empresa-alvo (dados reais da Receita Federal)");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        sb.AppendLine($"- CNPJ: {lead.Cnpj}");
        sb.AppendLine($"- Atividade (CNAE oficial): {Util.CnaeCatalogo.ParaPrompt(lead.Cnae)}");
        sb.AppendLine($"- UF/Município: {lead.Uf} / {lead.Municipio}");
        sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        sb.AppendLine($"- Situação cadastral: {lead.Situacao}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        sb.AppendLine();
        sb.AppendLine("Avalie a sinergia (0-100) e escreva um racional curto (1-3 frases).");
        return sb.ToString();
    }

    private static string ExtrairTextoDaResposta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    /// <summary>Parsing defensivo do JSON da IA. Nunca lança: degrada para um resultado seguro.</summary>
    private ResultadoClassificacao ParsearResultado(string texto, string cnpj)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return new ResultadoClassificacao(0, "Resposta vazia da IA.");

        var bruto = texto.Trim();
        // Caso o modelo envolva o JSON em ```json ... ``` ou texto extra.
        var m = BlocoJson().Match(bruto);
        if (m.Success) bruto = m.Value;

        try
        {
            using var doc = JsonDocument.Parse(bruto);
            var raiz = doc.RootElement;

            var score = LerInt(raiz, "score") ?? 0;
            score = Math.Clamp(score, 0, 100);

            var racional = raiz.TryGetProperty("racional", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(racional)) racional = "Sem racional retornado pela IA.";

            // Subnotas da rubrica de sinergia (quando presentes), com clamp por dimensão.
            var setor = ClampOuNull(LerInt(raiz, "setor"), 40);
            var porte = ClampOuNull(LerInt(raiz, "porte"), 25);
            var modelo = ClampOuNull(LerInt(raiz, "modelo"), 20);
            var geo = ClampOuNull(LerInt(raiz, "geografia") ?? LerInt(raiz, "geo"), 15);

            // Coerência: se todas as subnotas vieram, o score É a soma (nota auditável).
            if (setor is not null && porte is not null && modelo is not null && geo is not null)
            {
                var soma = setor.Value + porte.Value + modelo.Value + geo.Value;
                // red flag da tese pode ter rebaixado o score abaixo da soma — respeita o menor.
                score = Math.Min(Math.Clamp(soma, 0, 100), score > 0 ? score : soma);
            }

            return new ResultadoClassificacao(score, racional.Trim(), setor, porte, modelo, geo);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível parsear o JSON da IA para {Cnpj}. Resposta: {Texto}", cnpj, texto);
            return new ResultadoClassificacao(0, "Resposta da IA fora do formato esperado; lead não pontuado.");
        }
    }

    private static int? LerInt(JsonElement raiz, string prop)
    {
        if (!raiz.TryGetProperty(prop, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i)) return i;
        if (e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var s)) return s;
        return null;
    }

    private static int? ClampOuNull(int? v, int max) => v is null ? null : Math.Clamp(v.Value, 0, max);
}
