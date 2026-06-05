using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Classificacao;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Ingestao;
using MapeamentoMA.Web.Models;
using MapeamentoMA.Web.Scoring;

namespace MapeamentoMA.Web.Jobs;

public class CicloIngestaoService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CicloIngestaoService> _logger;
    private readonly TimeSpan _horario;

    private const string NomeJob = "CicloIngestao";

    public CicloIngestaoService(IServiceScopeFactory scopeFactory,
        IConfiguration config, ILogger<CicloIngestaoService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var horarioStr = config["Jobs:HorarioCiclo"] ?? "03:00";
        _horario = TimeSpan.TryParse(horarioStr, out var h) ? h : TimeSpan.FromHours(3);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var agora = DateTime.Now;
            var proxima = DateTime.Today.Add(_horario);
            if (proxima <= agora) proxima = proxima.AddDays(1);

            _logger.LogInformation("Próximo ciclo em {Proxima}", proxima);
            await Task.Delay(proxima - agora, ct);
            await ExecutarCicloAsync(ct);
        }
    }

    public async Task ExecutarCicloAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db            = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var classificador = scope.ServiceProvider.GetRequiredService<IClassificadorIA>();
        var scoring       = scope.ServiceProvider.GetRequiredService<MotorScoring>();
        var conectores    = scope.ServiceProvider.GetServices<IConectorFonte>().ToList();

        // Registra início da execução
        var execucao = new JobExecucao
        {
            NomeJob    = NomeJob,
            IniciadoEm = DateTime.UtcNow,
            Sucesso    = false
        };
        db.JobExecucoes.Add(execucao);
        await db.SaveChangesAsync(ct);

        try
        {
            // Determina janela: desde a última execução bem-sucedida (ou 24h)
            var ultimaSucesso = await db.JobExecucoes
                .Where(j => j.NomeJob == NomeJob && j.Sucesso)
                .OrderByDescending(j => j.IniciadoEm)
                .Select(j => (DateTime?)j.IniciadoEm)
                .FirstOrDefaultAsync(ct);

            var desde = ultimaSucesso ?? DateTime.UtcNow.AddHours(-24);
            _logger.LogInformation("Ciclo de ingestão — coletando desde {Desde}", desde);

            var empresas = await db.Empresas.AsNoTracking().ToListAsync(ct);
            int gatilhosCriados = 0;

            foreach (var conector in conectores)
            {
                _logger.LogInformation("Coletando de {Fonte}", conector.Nome);
                IReadOnlyList<ItemIngestao> itens;
                try { itens = await conector.ColetarAsync(desde, ct); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Conector {Fonte} falhou", conector.Nome);
                    continue;
                }

                _logger.LogInformation("{Qtd} itens de {Fonte}", itens.Count, conector.Nome);

                foreach (var item in itens)
                {
                    if (ct.IsCancellationRequested) break;

                    var empresa = EncontrarEmpresa(item.TextoBruto, empresas);
                    if (empresa is null) continue;

                    // Idempotência: hash do conteúdo + URL
                    var hash = ComputarHash(item.TextoBruto + item.Url);
                    if (await db.Gatilhos.AnyAsync(g => g.HashEvento == hash, ct)) continue;

                    var resultado = await classificador.ClassificarAsync(item.TextoBruto, ct);
                    if (resultado.NaoClassificado || !resultado.TemGatilho) continue;

                    var gatilho = new Gatilho
                    {
                        EmpresaId  = empresa.Id,
                        Tipo       = resultado.TipoGatilho ?? "Estresse",
                        DataEvento = DateOnly.FromDateTime(item.DataPublicacao),
                        Confianca  = resultado.Confianca,
                        Fonte      = item.Fonte,
                        Url        = item.Url,
                        HashEvento = hash,
                        Revisado   = false
                    };

                    db.Gatilhos.Add(gatilho);
                    await db.SaveChangesAsync(ct);
                    await scoring.RecalcularEmpresaAsync(empresa.Id, ct);
                    gatilhosCriados++;

                    _logger.LogInformation("Gatilho {Tipo} → empresa {Id}", gatilho.Tipo, empresa.Id);
                }
            }

            // Tarefa 3: alta prioridade = score ≥ 70 + gatilho não revisado nos últimos 30 dias
            var altaPrioridade = await PromoverAltaPrioridadeAsync(db, ct);

            execucao.GatilhosCriados = gatilhosCriados;
            execucao.AltaPrioridade  = altaPrioridade;
            execucao.Sucesso         = true;
            execucao.ConcluidoEm     = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Ciclo concluído — {G} gatilhos, {A} alta prioridade", gatilhosCriados, altaPrioridade);
        }
        catch (Exception ex)
        {
            execucao.Sucesso      = false;
            execucao.ErroMensagem = ex.Message;
            execucao.ConcluidoEm  = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _logger.LogError(ex, "Ciclo de ingestão falhou");
        }
    }

    private static async Task<int> PromoverAltaPrioridadeAsync(AppDbContext db, CancellationToken ct)
    {
        var limite = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));

        var empresasAlta = await db.PipelineItems
            .Include(p => p.Empresa)
            .ThenInclude(e => e.Scores)
            .Include(p => p.Empresa)
            .ThenInclude(e => e.Gatilhos)
            .Where(p => p.Estagio == "Identificado")
            .ToListAsync(ct);

        var candidatos = empresasAlta
            .Where(p =>
                p.Empresa.Scores.Any(s => s.Valor >= 70) &&
                p.Empresa.Gatilhos.Any(g => !g.Revisado && g.DataEvento >= limite))
            .OrderByDescending(p => p.Empresa.Scores.Max(s => s.Valor))
            .ToList();

        for (int i = 0; i < candidatos.Count; i++)
        {
            candidatos[i].Ordem = i;
        }

        // Empurra os demais para depois
        var restantes = empresasAlta.Except(candidatos).ToList();
        for (int i = 0; i < restantes.Count; i++)
            restantes[i].Ordem = candidatos.Count + i;

        await db.SaveChangesAsync(ct);
        return candidatos.Count;
    }

    private static Empresa? EncontrarEmpresa(string texto, List<Empresa> empresas)
    {
        var textoLower = texto.ToLowerInvariant();
        var cnpjNoTexto = new string(texto.Where(char.IsDigit).ToArray());

        foreach (var e in empresas)
        {
            if (!string.IsNullOrEmpty(e.Cnpj) && cnpjNoTexto.Contains(e.Cnpj))
                return e;
            if (!string.IsNullOrEmpty(e.RazaoSocial) && e.RazaoSocial.Length > 5 &&
                textoLower.Contains(e.RazaoSocial.ToLowerInvariant()))
                return e;
        }
        return null;
    }

    private static string ComputarHash(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}
