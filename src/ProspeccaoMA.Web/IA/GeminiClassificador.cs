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

        var prompt = MontarPrompt(lead, config);

        var corpo = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
        };

        var url = $"v1beta/models/{_opt.Modelo}:generateContent?key={_opt.ApiKey}";

        string textoResposta;
        try
        {
            var resp = await _http.PostAsync(url,
                new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            textoResposta = ExtrairTextoDaResposta(json);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha na chamada ao Gemini para o lead {Cnpj}", lead.Cnpj);
            return new ResultadoClassificacao(0, "Falha ao contatar a IA; lead mantido sem pontuação válida.");
        }

        return ParsearResultado(textoResposta, lead.Cnpj);
    }

    /// <summary>Prompt com SOMENTE dados reais e instrução anti-invenção (spec seção 4).</summary>
    private static string MontarPrompt(Lead lead, ConfiguracaoProspeccao config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A sell-side avaliando a sinergia de uma empresa-alvo REAL com um mandato.");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- NÃO invente informações. Avalie a sinergia somente com base nos dados fornecidos.");
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
