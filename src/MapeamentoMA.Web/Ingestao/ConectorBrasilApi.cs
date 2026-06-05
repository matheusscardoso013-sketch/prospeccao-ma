using System.Text.Json.Nodes;
using MapeamentoMA.Web.Models;

namespace MapeamentoMA.Web.Ingestao;

public class ConectorBrasilApi
{
    private readonly HttpClient _http;
    private readonly ILogger<ConectorBrasilApi> _logger;

    public ConectorBrasilApi(HttpClient http, ILogger<ConectorBrasilApi> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task EnriquecerAsync(Empresa empresa, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://brasilapi.com.br/api/cnpj/v1/{empresa.Cnpj}";
            var json = await _http.GetStringAsync(url, ct);
            var doc = JsonNode.Parse(json);
            if (doc is null) return;

            empresa.RazaoSocial       = doc["razao_social"]?.GetValue<string>() ?? empresa.RazaoSocial;
            empresa.CnaePrincipal     = LimparCnae(doc["cnae_fiscal"]?.ToString() ?? empresa.CnaePrincipal);
            empresa.Porte             = doc["porte"]?.GetValue<string>() ?? empresa.Porte;
            empresa.SituacaoCadastral = doc["descricao_situacao_cadastral"]?.GetValue<string>() ?? empresa.SituacaoCadastral;
            empresa.FonteOrigem       = "BrasilAPI";
            empresa.DataColeta        = DateTime.UtcNow;

            if (DateTime.TryParse(doc["data_inicio_atividade"]?.GetValue<string>(), out var abertura))
                empresa.DataAbertura = DateOnly.FromDateTime(abertura);
        }
        catch (HttpRequestException ex) when ((int?)ex.StatusCode == 404)
        {
            _logger.LogInformation("CNPJ {Cnpj} não encontrado na BrasilAPI", empresa.Cnpj);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enriquecer CNPJ {Cnpj} via BrasilAPI", empresa.Cnpj);
        }
    }

    private static string LimparCnae(string cnae)
        => new string(cnae.Where(char.IsDigit).ToArray()).TrimEnd('0').PadRight(7, '0')[..7];
}
