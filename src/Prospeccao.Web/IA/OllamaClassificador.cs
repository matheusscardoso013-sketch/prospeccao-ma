using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.IA;

/// <summary>
/// Implementação default da qualificação: Ollama LOCAL (grátis), em localhost:11434.
/// Envia só dados reais e exige JSON estrito; o parsing é defensivo — qualquer falha
/// vira "não pontuado" sem derrubar o ciclo.
/// </summary>
public class OllamaClassificador : IClassificadorIA
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaClassificador> _log;
    private readonly string _modelo;

    public OllamaClassificador(HttpClient http, IConfiguration config, ILogger<OllamaClassificador> log)
    {
        _http = http;
        _log = log;
        _modelo = config["Ollama:Modelo"] ?? "qwen2.5:3b";
    }

    public async Task<ResultadoQualificacao> QualificarAsync(
        Lead lead, ConfiguracaoProspeccao config, CancellationToken ct = default)
    {
        try
        {
            var prompt = MontarPrompt(lead, config);
            var corpo = new
            {
                model = _modelo,
                prompt,
                format = "json",
                stream = false
            };

            using var resp = await _http.PostAsJsonAsync("/api/generate", corpo, ct);
            if (!resp.IsSuccessStatusCode)
                return ResultadoQualificacao.Falha($"(não pontuado: Ollama HTTP {(int)resp.StatusCode})");

            var body = await resp.Content.ReadAsStringAsync(ct);
            return Interpretar(body);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Falha ao qualificar lead {Cnpj} via Ollama", lead.Cnpj);
            return ResultadoQualificacao.Falha("(não pontuado: erro ao chamar a IA)");
        }
    }

    /// <summary>Prompt com SOMENTE dados reais do candidato e instrução anti-invenção.</summary>
    private static string MontarPrompt(Lead lead, ConfiguracaoProspeccao config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A sell-side. Avalie a sinergia de uma empresa-alvo");
        sb.AppendLine("com os critérios de prospecção, usando SOMENTE os dados fornecidos abaixo.");
        sb.AppendLine("NÃO invente informações. Se faltar dado, diga que falta no racional.");
        sb.AppendLine();
        sb.AppendLine("CRITÉRIOS DE PROSPECÇÃO:");
        sb.AppendLine($"- CNAEs-alvo: {config.Cnaes}");
        sb.AppendLine($"- UFs-alvo: {config.Ufs}");
        sb.AppendLine($"- Capital social desejado: {Faixa(config.CapitalMin, config.CapitalMax)}");
        sb.AppendLine();
        sb.AppendLine("EMPRESA-ALVO (dados reais da Receita Federal):");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        sb.AppendLine($"- CNAE: {Ou(lead.Cnae)}");
        sb.AppendLine($"- UF: {Ou(lead.Uf)}");
        sb.AppendLine($"- Município: {Ou(lead.Municipio)}");
        sb.AppendLine($"- Capital social: {(lead.CapitalSocial.HasValue ? lead.CapitalSocial.Value.ToString("C", new CultureInfo("pt-BR")) : "não informado")}");
        sb.AppendLine($"- Situação cadastral: {Ou(lead.Situacao)}");
        sb.AppendLine($"- Porte (estimado): {Ou(lead.PorteEstimado)}");
        sb.AppendLine();
        sb.AppendLine("Responda APENAS com um JSON neste formato exato, sem texto fora dele:");
        sb.AppendLine("{\"score\": <inteiro de 0 a 100>, \"racional\": \"<frase curta em português>\"}");
        return sb.ToString();
    }

    private static string Ou(string? v) => string.IsNullOrWhiteSpace(v) ? "não informado" : v;

    private static string Faixa(decimal? min, decimal? max)
    {
        var c = new CultureInfo("pt-BR");
        if (min.HasValue && max.HasValue) return $"entre {min.Value.ToString("C", c)} e {max.Value.ToString("C", c)}";
        if (min.HasValue) return $"a partir de {min.Value.ToString("C", c)}";
        if (max.HasValue) return $"até {max.Value.ToString("C", c)}";
        return "sem faixa definida";
    }

    /// <summary>Extrai score+racional do corpo do Ollama, com tolerância a falhas.</summary>
    private ResultadoQualificacao Interpretar(string body)
    {
        try
        {
            using var externo = JsonDocument.Parse(body);
            if (!externo.RootElement.TryGetProperty("response", out var respEl))
                return ResultadoQualificacao.Falha("(não pontuado: resposta sem campo 'response')");

            var textoJson = respEl.GetString();
            if (string.IsNullOrWhiteSpace(textoJson))
                return ResultadoQualificacao.Falha("(não pontuado: resposta vazia da IA)");

            using var interno = JsonDocument.Parse(textoJson);
            var raiz = interno.RootElement;

            var score = ExtrairScore(raiz);
            var racional = raiz.TryGetProperty("racional", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() ?? string.Empty
                : string.Empty;

            return new ResultadoQualificacao
            {
                Sucesso = true,
                Score = score,
                Racional = string.IsNullOrWhiteSpace(racional) ? "(sem racional)" : racional.Trim()
            };
        }
        catch (JsonException)
        {
            return ResultadoQualificacao.Falha("(não pontuado: JSON inválido da IA)");
        }
    }

    private static int ExtrairScore(JsonElement raiz)
    {
        if (!raiz.TryGetProperty("score", out var s))
            return 0;

        int valor = s.ValueKind switch
        {
            JsonValueKind.Number => (int)Math.Round(s.GetDouble()),
            JsonValueKind.String => int.TryParse(s.GetString(), out var n) ? n : 0,
            _ => 0
        };
        return Math.Clamp(valor, 0, 100);
    }
}
