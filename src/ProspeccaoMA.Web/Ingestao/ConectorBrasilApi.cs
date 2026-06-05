using System.Net;

namespace ProspeccaoMA.Web.Ingestao;

public interface IConectorBrasilApi
{
    Task<EmpresaBrasilApi?> ConsultarAsync(string cnpj, CancellationToken ct = default);
}

/// <summary>
/// Enriquecimento pontual por CNPJ via BrasilAPI (dados REAIS). Respeita rate limit
/// com intervalo mínimo entre chamadas e retry com backoff exponencial em 429/5xx.
/// NÃO faz busca reversa por setor — só consulta um CNPJ já selecionado.
/// </summary>
public class ConectorBrasilApi : IConectorBrasilApi
{
    private readonly HttpClient _http;
    private readonly ILogger<ConectorBrasilApi> _log;
    private static readonly SemaphoreSlim _porta = new(1, 1);
    private static DateTime _ultimaChamada = DateTime.MinValue;

    // Intervalo mínimo entre chamadas para respeitar o rate limit da API gratuita.
    private static readonly TimeSpan IntervaloMinimo = TimeSpan.FromMilliseconds(700);
    private const int MaxTentativas = 4;

    public ConectorBrasilApi(HttpClient http, ILogger<ConectorBrasilApi> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<EmpresaBrasilApi?> ConsultarAsync(string cnpj, CancellationToken ct = default)
    {
        var limpo = CnpjUtil.Normalizar(cnpj);
        if (limpo is null)
        {
            _log.LogWarning("CNPJ inválido ignorado no enriquecimento: {Cnpj}", cnpj);
            return null;
        }

        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            await RespeitarRateLimitAsync(ct);
            try
            {
                var resp = await _http.GetAsync($"api/cnpj/v1/{limpo}", ct);

                if (resp.StatusCode == HttpStatusCode.NotFound)
                    return null; // CNPJ não encontrado na base da API

                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    await EsperarBackoffAsync(tentativa, ct);
                    continue;
                }

                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadFromJsonAsync<EmpresaBrasilApi>(cancellationToken: ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && tentativa < MaxTentativas)
            {
                _log.LogWarning(ex, "Falha ao consultar BrasilAPI (tentativa {N}) para {Cnpj}", tentativa, limpo);
                await EsperarBackoffAsync(tentativa, ct);
            }
        }

        _log.LogError("Esgotadas as tentativas de enriquecimento para o CNPJ {Cnpj}", limpo);
        return null;
    }

    private static async Task RespeitarRateLimitAsync(CancellationToken ct)
    {
        await _porta.WaitAsync(ct);
        try
        {
            var decorrido = DateTime.UtcNow - _ultimaChamada;
            if (decorrido < IntervaloMinimo)
                await Task.Delay(IntervaloMinimo - decorrido, ct);
            _ultimaChamada = DateTime.UtcNow;
        }
        finally
        {
            _porta.Release();
        }
    }

    private static Task EsperarBackoffAsync(int tentativa, CancellationToken ct)
        => Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, tentativa)), ct);
}
