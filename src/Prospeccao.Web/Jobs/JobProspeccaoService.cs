namespace Prospeccao.Web.Jobs;

/// <summary>
/// Hosted Service que dispara a rotina de prospecção todo dia no horário configurado
/// (padrão 12h, seção 4 da spec). Mantém o banco ativo e gera os leads do dia.
/// </summary>
public class JobProspeccaoService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<JobProspeccaoService> _log;
    private readonly int _hora;

    public JobProspeccaoService(IServiceProvider services, IConfiguration config,
        ILogger<JobProspeccaoService> log)
    {
        _services = services;
        _log = log;
        _hora = config.GetValue<int?>("Prospeccao:HoraExecucao") ?? 12;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = TempoAteProximaExecucao();
            _log.LogInformation("Próxima rotina de prospecção em {Espera} (às {Hora}h).", espera, _hora);

            try { await Task.Delay(espera, stoppingToken); }
            catch (TaskCanceledException) { break; }

            try
            {
                using var scope = _services.CreateScope();
                var rotina = scope.ServiceProvider.GetRequiredService<RotinaProspeccao>();
                await rotina.ExecutarAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao executar a rotina agendada de prospecção");
            }
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
