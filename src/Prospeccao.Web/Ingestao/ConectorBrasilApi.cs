using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.Ingestao;

/// <summary>
/// Enriquecimento pontual de um CNPJ via BrasilAPI (dados públicos reais da Receita).
/// Respeita rate limit com retry/backoff. NÃO faz busca por setor — só consulta por CNPJ.
/// </summary>
public class ConectorBrasilApi
{
    public const string FonteNome = "Receita Federal — base pública (via BrasilAPI)";

    private readonly HttpClient _http;
    private readonly ILogger<ConectorBrasilApi> _log;

    public ConectorBrasilApi(HttpClient http, ILogger<ConectorBrasilApi> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Busca os dados reais do CNPJ e devolve um <see cref="Lead"/> preenchido,
    /// ou null se o CNPJ não for encontrado / a API falhar após as tentativas.
    /// </summary>
    public async Task<Lead?> EnriquecerAsync(string cnpj14, CancellationToken ct = default)
    {
        const int maxTentativas = 3;
        for (var tentativa = 1; tentativa <= maxTentativas; tentativa++)
        {
            try
            {
                using var resp = await _http.GetAsync($"/api/cnpj/v1/{cnpj14}", ct);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return null; // CNPJ inexistente — não é erro de rede

                if (resp.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)resp.StatusCode >= 500)
                {
                    await EsperarBackoffAsync(tentativa, ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                    return null;

                var dto = await resp.Content.ReadFromJsonAsync<BrasilApiCnpj>(cancellationToken: ct);
                if (dto is null) return null;

                return new Lead
                {
                    Cnpj = cnpj14,
                    RazaoSocial = dto.RazaoSocial ?? "(sem razão social)",
                    Cnae = dto.CnaeFiscal?.ToString(),
                    Uf = dto.Uf,
                    Municipio = dto.Municipio,
                    CapitalSocial = dto.CapitalSocial,
                    Situacao = dto.SituacaoCadastral,
                    PorteEstimado = dto.Porte,
                    Contato = MontarContato(dto)
                };
            }
            catch (Exception ex) when (tentativa < maxTentativas)
            {
                _log.LogWarning(ex, "Tentativa {N} falhou ao consultar CNPJ {Cnpj}", tentativa, cnpj14);
                await EsperarBackoffAsync(tentativa, ct);
            }
        }
        return null;
    }

    private static async Task EsperarBackoffAsync(int tentativa, CancellationToken ct)
    {
        // Backoff exponencial simples: 0,5s, 1s, 2s...
        var ms = (int)(500 * Math.Pow(2, tentativa - 1));
        await Task.Delay(ms, ct);
    }

    private static string? MontarContato(BrasilApiCnpj dto)
    {
        var partes = new[] { dto.Telefone, dto.Email }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var contato = string.Join(" / ", partes);
        return string.IsNullOrWhiteSpace(contato) ? null : contato;
    }

    /// <summary>Subconjunto dos campos da resposta da BrasilAPI que usamos.</summary>
    private sealed class BrasilApiCnpj
    {
        [JsonPropertyName("razao_social")] public string? RazaoSocial { get; set; }
        [JsonPropertyName("cnae_fiscal")] public long? CnaeFiscal { get; set; }
        [JsonPropertyName("uf")] public string? Uf { get; set; }
        [JsonPropertyName("municipio")] public string? Municipio { get; set; }
        [JsonPropertyName("capital_social")] public decimal? CapitalSocial { get; set; }
        [JsonPropertyName("descricao_situacao_cadastral")] public string? SituacaoCadastral { get; set; }
        [JsonPropertyName("porte")] public string? Porte { get; set; }
        [JsonPropertyName("ddd_telefone_1")] public string? Telefone { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }
}
