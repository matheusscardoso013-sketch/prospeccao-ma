using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Util;

namespace ProspeccaoMA.Web.Jobs;

/// <summary>
/// Agendador da rotina diária (padrão 12h, spec seção 4) com RECUPERAÇÃO DE DIA PERDIDO.
///
/// Por que não basta "dormir até as 12h": no free tier do Render o app hiberna após ~15 min
/// ocioso. Se ele acorda às 13h, a versão antiga deste serviço agendava para as 12h de
/// AMANHÃ — e se hibernasse de novo antes disso, o dia inteiro se perdia em silêncio. Foi
/// assim que a plataforma ficou 9 dias parada (19/07→28/07) sem ninguém perceber: o gatilho
/// externo falhou e não havia nada dentro do app para cobrir.
///
/// Agora, a cada volta do laço (inclusive na PARTIDA, ou seja, em todo despertar) o serviço
/// pergunta ao banco se a rodada de hoje já saiu. Se passou do horário e não saiu, roda na
/// hora. Assim qualquer coisa que acorde o app — o cron externo, o keep-alive, alguém do
/// time abrindo o painel — também conserta o dia. A rede não depende de nenhuma máquina
/// específica: mora na própria plataforma.
/// </summary>
public class JobProspeccaoService : BackgroundService
{
    private readonly IServiceScopeFactory _escopos;
    private readonly ILogger<JobProspeccaoService> _log;
    private readonly int _hora;

    /// <summary>Teto da espera: mesmo faltando muito para as 12h, o serviço reavalia de tempos
    /// em tempos. Sem isso, um app que ficasse acordado desde antes do horário só olharia o
    /// relógio uma vez.</summary>
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromMinutes(15);

    /// <summary>Tentativas por dia antes de desistir: um dia com falha real (cota estourada,
    /// por exemplo) não deve virar laço infinito queimando o que sobrou da cota.</summary>
    private const int MaxTentativasPorDia = 3;

    public JobProspeccaoService(IServiceScopeFactory escopos, ILogger<JobProspeccaoService> log, IConfiguration cfg)
    {
        _escopos = escopos;
        _log = log;
        _hora = cfg.GetValue("Prospeccao:HoraExecucao", 12);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("JobProspeccao ativo. Execução diária às {Hora}h (com recuperação de dia perdido).", _hora);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RecuperarDiaPerdidoAsync(stoppingToken);

            var ateOHorario = TempoAteProximaExecucao();
            var espera = ateOHorario < EsperaMaxima ? ateOHorario : EsperaMaxima;

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // aplicação encerrando
            }

            // Chegou a hora exata: roda. Fora disso, o laço volta e a recuperação decide.
            if (espera == ateOHorario)
                await RodarUmaVezAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Coração da rede de segurança: se já passou do horário de hoje e nenhuma rodada saiu
    /// com sucesso, executa agora. Consulta o banco (não a memória do processo) justamente
    /// porque o processo morre a cada hibernação — o histórico é a única fonte confiável.
    /// </summary>
    private async Task RecuperarDiaPerdidoAsync(CancellationToken ct)
    {
        try
        {
            if (Fuso.Agora.Hour < _hora) return; // ainda não deu a hora

            using var escopo = _escopos.CreateScope();
            var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
            var desdeMeiaNoite = Fuso.InicioHojeUtc();

            var doDia = await db.ExecucoesJob
                .Where(e => e.IniciadoEm >= desdeMeiaNoite)
                .Select(e => e.Status)
                .ToListAsync(ct);

            if (doDia.Any(s => s == StatusExecucao.Sucesso)) return;      // o dia está feito
            if (doDia.Any(s => s == StatusExecucao.EmAndamento)) return;  // já tem uma rodando
            if (doDia.Count >= MaxTentativasPorDia)
            {
                _log.LogWarning("Rodada de hoje falhou {N}x — não vou insistir mais hoje.", doDia.Count);
                return;
            }

            _log.LogWarning("Rodada de hoje ainda não saiu (já são {Hora}h) — recuperando agora. " +
                            "Tentativas anteriores hoje: {N}.", Fuso.Agora.Hour, doDia.Count);
            await RodarUmaVezAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // encerrando — silencioso
        }
        catch (Exception ex)
        {
            // A recuperação nunca pode derrubar o serviço: sem ela o app ainda roda no horário.
            _log.LogError(ex, "Falha ao verificar/recuperar a rodada do dia.");
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
        var agora = Fuso.Agora;
        var proxima = agora.Date.AddHours(_hora);
        if (proxima <= agora) proxima = proxima.AddDays(1);
        return proxima - agora;
    }
}
