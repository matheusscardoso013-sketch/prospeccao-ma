using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProspeccaoMA.Web.Jobs;

namespace ProspeccaoMA.Web.Controllers;

/// <summary>
/// Endpoint de disparo da prospecção para um cron externo (ex.: cron-job.org) chamar
/// no horário de Brasília — isso acorda o app (free tier hiberna) E executa a rotina.
/// Protegido por token (Prospeccao:TokenJob, via env var). Responde na hora e roda a
/// rotina em segundo plano para não esbarrar no timeout do cron.
/// </summary>
[AllowAnonymous]
public class JobController : Controller
{
    private readonly IServiceScopeFactory _escopos;
    private readonly IConfiguration _cfg;
    private readonly ILogger<JobController> _log;

    public JobController(IServiceScopeFactory escopos, IConfiguration cfg, ILogger<JobController> log)
    {
        _escopos = escopos;
        _cfg = cfg;
        _log = log;
    }

    [HttpGet]
    [HttpPost]
    public IActionResult Disparar(string? token)
    {
        var esperado = _cfg["Prospeccao:TokenJob"];
        if (string.IsNullOrWhiteSpace(esperado) || !string.Equals(token, esperado, StringComparison.Ordinal))
        {
            _log.LogWarning("Tentativa de disparo do job com token inválido.");
            return Unauthorized("Token inválido.");
        }

        // Fire-and-forget com escopo próprio (o request retorna imediatamente).
        _ = Task.Run(async () =>
        {
            try
            {
                using var escopo = _escopos.CreateScope();
                var rotina = escopo.ServiceProvider.GetRequiredService<RotinaProspeccao>();
                await rotina.ExecutarAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao executar a rotina disparada via endpoint");
            }
        });

        return Content("Prospecção disparada. Acompanhe em Execuções.");
    }
}
