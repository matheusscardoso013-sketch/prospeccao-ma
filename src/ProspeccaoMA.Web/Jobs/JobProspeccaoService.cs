namespace ProspeccaoMA.Web.Jobs;

/// <summary>
/// Hosted Service que dispara a rotina de prospecção todo dia no horário configurado
/// (padrão 12h, spec seção 4). Calcula o tempo até a próxima execução e aguarda; após
/// rodar, agenda o próximo dia. A RotinaProspeccao é resolvida em um escopo próprio
/// porque o BackgroundService é singleton e o DbContext é scoped.
/// </summary>
public class JobProspeccaoService : BackgroundService
{
    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<JobProspeccaoService> _log;
    private readonly int _hora;

    public JobProspeccaoService(IServiceScopeFactory escopos, ILogger<JobProspeccaoService> log, IConfiguration cfg)
    {
        _escopos = escopos;
        _log = log;
        _hora = cfg.GetValue("Prospeccao:HoraExecucao", 12);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("JobProspeccao ativo. Execução diária às {Hora}h.", _hora);

        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = TempoAteProximaExecucao();
            _log.LogInformation("Próxima prospecção em {Espera} (às {Hora}h).", espera, _hora);

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // aplicação encerrando
            }

            await RodarUmaVezAsync(stoppingToken);
        }
    }

    private async Task RodarUmaVezAsync(CancellationToken ct)
    {
        try
        {
            using var escopo = _escopos.CreateScope();
            var rotina = escopo.ServiceProvider.GetRequiredService<RotinaProspeccao>();
            await rotina.ExecutarAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // encerrando — silencioso
        }
        catch (Exception ex)
        {
            // A RotinaProspeccao já registra o erro em ExecucoesJob; aqui só evitamos derrubar o serviço.
            _log.LogError(ex, "Falha não tratada ao executar a rotina diária");
        }
    }

    private TimeSpan TempoAteProximaExecucao()
    {
        var agora = DateTime.Now;
        var proxima = agora.Date.AddHours(_hora);
        if (proxima <= agora)
            proxima = proxima.AddDays(1);
        return proxima - agora;
    }
}
