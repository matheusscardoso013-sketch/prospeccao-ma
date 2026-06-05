using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MapeamentoMA.Web.Classificacao;

public class OllamaClassificador : IClassificadorIA
{
    private readonly HttpClient _http;
    private readonly string _modelo;
    private readonly ILogger<OllamaClassificador> _logger;

    private static readonly string Prompt = """
        Analise o texto abaixo e retorne APENAS um JSON válido, sem texto adicional, no formato:
        {
          "empresa": "string ou null",
          "cnpj": "string ou null (somente dígitos)",
          "tese_sugerida": "string ou null",
          "tem_gatilho": true ou false,
          "tipo_gatilho": "Sucessao|Estresse|Captacao|Consolidacao|Regulatorio ou null",
          "valor_envolvido": 0,
          "valor_estimado": false,
          "confianca": 0.0
        }
        Texto:
        """;

    public OllamaClassificador(HttpClient http, IConfiguration config, ILogger<OllamaClassificador> logger)
    {
        _http = http;
        _modelo = config["Ollama:Modelo"] ?? "qwen2.5:7b";
        _logger = logger;
    }

    public async Task<ResultadoClassificacao> ClassificarAsync(string textoBruto, CancellationToken ct = default)
    {
        try
        {
            var payload = new
            {
                model = _modelo,
                prompt = Prompt + textoBruto,
                format = "json",
                stream = false
            };

            var json = JsonSerializer.Serialize(payload);
            var response = await _http.PostAsync("/api/generate",
                new StringContent(json, Encoding.UTF8, "application/json"), ct);

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            var doc = JsonNode.Parse(body);
            var responseText = doc?["response"]?.GetValue<string>() ?? "";

            return ParseResposta(responseText, textoBruto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao classificar via Ollama. Item marcado como não classificado.");
            return NaoClassificado(textoBruto);
        }
    }

    private ResultadoClassificacao ParseResposta(string json, string textoBruto)
    {
        try
        {
            var doc = JsonNode.Parse(json);
            if (doc is null) return NaoClassificado(textoBruto);

            var tipoGatilho = doc["tipo_gatilho"]?.GetValue<string>();
            var tiposValidos = new[] { "Sucessao", "Estresse", "Captacao", "Consolidacao", "Regulatorio" };
            if (tipoGatilho is not null && !tiposValidos.Contains(tipoGatilho))
                tipoGatilho = null;

            return new ResultadoClassificacao
            {
                Empresa      = doc["empresa"]?.GetValue<string>(),
                Cnpj         = doc["cnpj"]?.GetValue<string>(),
                TeseSugerida = doc["tese_sugerida"]?.GetValue<string>(),
                TemGatilho   = doc["tem_gatilho"]?.GetValue<bool>() ?? false,
                TipoGatilho  = tipoGatilho,
                ValorEnvolvido = doc["valor_envolvido"] is JsonNode vn
                    ? (decimal)vn.GetValue<double>() : 0,
                ValorEstimado = doc["valor_estimado"]?.GetValue<bool>() ?? false,
                Confianca    = doc["confianca"]?.GetValue<double>() ?? 0.0,
            };
        }
        catch
        {
            return NaoClassificado(textoBruto);
        }
    }

    private static ResultadoClassificacao NaoClassificado(string texto) => new()
    {
        NaoClassificado = true,
        TextoBrutoPreservado = texto,
        TemGatilho = false,
        Confianca = 0
    };
}
