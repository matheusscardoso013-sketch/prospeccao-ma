using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.IA;

public class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Modelo { get; set; } = "gemini-2.5-flash";
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
        Lead lead, Comprador comprador, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            return new ResultadoClassificacao(0, "IA não configurada: defina Gemini:ApiKey.");
        return await ChamarAsync(MontarPromptSinergia(lead, comprador), $"{lead.Cnpj}~{comprador.Nome}", ct);
    }

    // Espaçamento mínimo entre chamadas (free tier ~15 req/min) para evitar rajadas/429.
    private static readonly SemaphoreSlim _porta = new(1, 1);
    private static DateTime _ultima = DateTime.MinValue;
    private static readonly TimeSpan IntervaloMin = TimeSpan.FromMilliseconds(1500);
    private const int MaxTentativas = 4;

    /// <summary>Chamada genérica ao Gemini (JSON estrito) + parsing defensivo. Nunca lança.
    /// Resiliente a rate limit (429) e erros transitórios (5xx): retry com backoff.</summary>
    private async Task<ResultadoClassificacao> ChamarAsync(string prompt, string idLog, CancellationToken ct)
    {
        var corpo = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
        };
        var url = $"v1beta/models/{_opt.Modelo}:generateContent?key={_opt.ApiKey}";

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
                    return new ResultadoClassificacao(0, "IA indisponível no momento (limite de uso); tente novamente.");
                }

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                return ParsearResultado(ExtrairTextoDaResposta(json), idLog);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && tentativa < MaxTentativas)
            {
                _log.LogWarning(ex, "Falha transitória no Gemini (tentativa {N}, {Id})", tentativa, idLog);
                await BackoffAsync(tentativa, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha na chamada ao Gemini ({Id})", idLog);
                return new ResultadoClassificacao(0, "Falha ao contatar a IA.");
            }
        }
        return new ResultadoClassificacao(0, "IA indisponível no momento; tente novamente.");
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
    private static string MontarPromptSinergia(Lead lead, Comprador comprador)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A buy-side avaliando o fit entre uma empresa-alvo REAL e a TESE de investimento de um comprador.");
        sb.AppendLine("Dê uma nota de sinergia (0-100) de quão bem o alvo se encaixa na tese do comprador (setor, porte, modelo, geografia).");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- NÃO invente informações. Avalie só com base nos dados fornecidos.");
        sb.AppendLine("- Porte/faturamento do alvo são ESTIMADOS (capital social é proxy) — não trate como receita real.");
        sb.AppendLine("- Se faltar dado relevante, diga no racional.");
        sb.AppendLine("- Responda ESTRITAMENTE em JSON: {\"score\": <inteiro 0-100>, \"racional\": \"<texto curto>\"}.");
        sb.AppendLine();
        sb.AppendLine("## Comprador e sua tese");
        sb.AppendLine($"- Nome: {comprador.Nome}");
        if (!string.IsNullOrWhiteSpace(comprador.TipoEmpresa)) sb.AppendLine($"- Tipo: {comprador.TipoEmpresa}");
        if (!string.IsNullOrWhiteSpace(comprador.Segmento)) sb.AppendLine($"- Segmento: {comprador.Segmento}");
        if (!string.IsNullOrWhiteSpace(comprador.FaixaFaturamento)) sb.AppendLine($"- Faixa de faturamento alvo: {comprador.FaixaFaturamento}");
        if (!string.IsNullOrWhiteSpace(comprador.Tags)) sb.AppendLine($"- Tags da tese: {comprador.Tags}");
        sb.AppendLine($"- Tese: {Resumir(comprador.Tese, 1500)}");
        sb.AppendLine();
        sb.AppendLine($"## Empresa-alvo (dados reais — {lead.Origem})");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        if (!string.IsNullOrWhiteSpace(lead.Cnae))
            sb.AppendLine($"- CNAE: {lead.Cnae}");
        if (!string.IsNullOrWhiteSpace(lead.Uf))
            sb.AppendLine($"- UF/Município: {lead.Uf}/{lead.Municipio}");
        if (!string.IsNullOrWhiteSpace(lead.Segmento))
            sb.AppendLine($"- Segmento: {lead.Segmento}");
        if (lead.CapitalSocial > 0)
            sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        if (!string.IsNullOrWhiteSpace(lead.Situacao))
            sb.AppendLine($"- Situação: {lead.Situacao}");
        if (!string.IsNullOrWhiteSpace(lead.Descricao))
            sb.AppendLine($"- Resumo da empresa: {Resumir(lead.Descricao, 1200)}");
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
        sb.AppendLine("Avalie o potencial desta empresa-alvo REAL como candidata a uma operação de VENDA (sell-side),");
        sb.AppendLine("considerando aderência setorial ao mandato, porte de middle market e atratividade para compradores.");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- NÃO invente informações. Avalie somente com base nos dados fornecidos.");
        sb.AppendLine("- Lembre que porte/faturamento são ESTIMADOS (capital social é proxy) — não trate como receita real.");
        sb.AppendLine("- Se faltar dado relevante, diga explicitamente que falta no racional.");
        sb.AppendLine("- Responda ESTRITAMENTE em JSON no formato: {\"score\": <inteiro 0-100>, \"racional\": \"<texto curto>\"}.");
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
        sb.AppendLine($"- CNAE: {lead.Cnae}");
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

            var score = 0;
            if (raiz.TryGetProperty("score", out var s))
            {
                if (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var si)) score = si;
                else if (s.ValueKind == JsonValueKind.String && int.TryParse(s.GetString(), out var ss)) score = ss;
            }
            score = Math.Clamp(score, 0, 100);

            var racional = raiz.TryGetProperty("racional", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(racional)) racional = "Sem racional retornado pela IA.";

            return new ResultadoClassificacao(score, racional.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível parsear o JSON da IA para {Cnpj}. Resposta: {Texto}", cnpj, texto);
            return new ResultadoClassificacao(0, "Resposta da IA fora do formato esperado; lead não pontuado.");
        }
    }
}
