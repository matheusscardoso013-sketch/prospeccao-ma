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
public class JobsController : Controller
{
    private readonly IServiceScopeFactory _escopos;
    private readonly IConfiguration _cfg;
    private readonly ILogger<JobsController> _log;

    public JobsController(IServiceScopeFactory escopos, IConfiguration cfg, ILogger<JobsController> log)
    {
        _escopos = escopos;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>
    /// Sinal de vida da rotina, para um vigia externo (GitHub Actions) conferir sem token:
    /// diz apenas SE a rodada de hoje saiu e quando foi a última — nenhum dado de negócio.
    /// Chamar isto já acorda o app, e ao acordar o JobProspeccaoService recupera o dia
    /// perdido sozinho; o vigia só precisa checar de novo e gritar se continuar sem rodada.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Saude(CancellationToken ct)
    {
        using var escopo = _escopos.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<Data.AppDbContext>();

        var desdeMeiaNoite = Util.Fuso.InicioHojeUtc();
        var hoje = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .ToListAsync(db.ExecucoesJob.Where(e => e.IniciadoEm >= desdeMeiaNoite)
                .Select(e => new { e.Status, e.IniciadoEm }), ct);
        var ultima = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.ExecucoesJob
                .Where(e => e.Status == Models.StatusExecucao.Sucesso)
                .OrderByDescending(e => e.IniciadoEm)
                .Select(e => new { e.IniciadoEm, e.LeadsGerados }), ct);

        return Json(new
        {
            rodouHoje = hoje.Any(e => e.Status == Models.StatusExecucao.Sucesso),
            emAndamento = hoje.Any(e => e.Status == Models.StatusExecucao.EmAndamento),
            tentativasHoje = hoje.Count,
            ultimaEm = ultima is null ? null : Util.Fuso.Brasil(ultima.IniciadoEm).ToString("yyyy-MM-dd HH:mm"),
            ultimaLeads = ultima?.LeadsGerados,
            agora = Util.Fuso.Agora.ToString("yyyy-MM-dd HH:mm")
        });
    }

    [HttpGet]
    [HttpPost]
    public IActionResult Disparar(string? token, string? modo = null)
    {
        var esperado = _cfg["Prospeccao:TokenJob"];
        if (string.IsNullOrWhiteSpace(esperado) || !string.Equals(token, esperado, StringComparison.Ordinal))
        {
            _log.LogWarning("Tentativa de disparo do job com token inválido.");
            return Unauthorized("Token inválido.");
        }

        // modo=falhas: só reavalia pontuações que falharam, sem rodar a prospecção do dia.
        // modo=curados: só cruza um lote de alvos curados pendentes (backfill sob demanda).
        // modo=email: só (re)envia o resumo do dia — teste de SMTP sem gastar cota de IA.
        var soFalhas = string.Equals(modo, "falhas", StringComparison.OrdinalIgnoreCase);
        var soCurados = string.Equals(modo, "curados", StringComparison.OrdinalIgnoreCase);
        var soEmail = string.Equals(modo, "email", StringComparison.OrdinalIgnoreCase);

        // Lotes pequenos (config Prospeccao:*) para cada disparo terminar em poucos
        // minutos — no free tier a instância hiberna e mataria uma rodada longa no meio.
        // O cron externo dispara modo=curados de hora em hora e vai drenando a fila aos poucos.
        var loteCurados = _cfg.GetValue("Prospeccao:AlvosCuradosPorDia", 4);
        var loteFalhas = _cfg.GetValue("Prospeccao:ReprocessarFalhasPorDia", 4);

        // Fire-and-forget com escopo próprio (o request retorna imediatamente).
        _ = Task.Run(async () =>
        {
            try
            {
                using var escopo = _escopos.CreateScope();
                var rotina = escopo.ServiceProvider.GetRequiredService<RotinaProspeccao>();
                if (soEmail)
                {
                    var email = escopo.ServiceProvider.GetRequiredService<Notificacoes.INotificadorEmail>();
                    await email.EnviarResumoDiarioAsync(CancellationToken.None);
                }
                else if (soFalhas)
                    await rotina.ReprocessarFalhasAsync(loteFalhas, CancellationToken.None);
                else if (soCurados)
                {
                    // Esteira horária: SÓ drena a fila de curados (finita — some quando acaba).
                    // A cura de falhas fica com a rodada do meio-dia (ReprocessarFalhasPorDia),
                    // senão a esteira, rodando 24x/dia, canibalizaria a cota diária (~80 req
                    // somando a rotação de modelos) e a rodada de leads ficaria sem cota.
                    await rotina.CruzarCuradosPendentesAsync(loteCurados, CancellationToken.None);
                }
                else
                    await rotina.ExecutarAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao executar a rotina disparada via endpoint");
            }
        });

        return Content(soEmail
            ? "Resumo do dia disparado por e-mail (se houver novidades e o SMTP estiver configurado)."
            : soFalhas
                ? $"Reprocessamento de falhas disparado (lote de até {loteFalhas})."
                : soCurados
                    ? $"Backfill de alvos curados disparado (lote de até {loteCurados})."
                    : "Prospecção disparada. Acompanhe em Execuções.");
    }
}
