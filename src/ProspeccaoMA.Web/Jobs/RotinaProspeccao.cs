using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Jobs;

/// <summary>
/// Rotina de prospecção (spec seção 4). Idempotente: só pontua leads REAIS que casam
/// com cada configuração ativa e que ainda não têm score para aquela configuração
/// (dedup via índice único LeadId+ConfiguracaoId). Não descobre/inventa empresas —
/// trabalha sobre os Leads já importados da base da Receita.
/// Usada tanto pelo BackgroundService das 12h quanto pelo botão "Rodar agora".
/// </summary>
public class RotinaProspeccao
{
    private readonly AppDbContext _db;
    private readonly IClassificadorIA _ia;
    private readonly ILogger<RotinaProspeccao> _log;
    private readonly int _tamanhoLote;

    private const string FonteReceita = "Receita Federal — base pública";

    public RotinaProspeccao(AppDbContext db, IClassificadorIA ia, ILogger<RotinaProspeccao> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _log = log;
        _tamanhoLote = cfg.GetValue("Prospeccao:TamanhoLote", 100);
    }

    public async Task<ExecucaoJob> ExecutarAsync(CancellationToken ct = default)
    {
        var execucao = new ExecucaoJob { IniciadoEm = DateTime.UtcNow, Status = StatusExecucao.EmAndamento };
        _db.ExecucoesJob.Add(execucao);
        await _db.SaveChangesAsync(ct);

        var totalNovos = 0;
        try
        {
            var configs = await _db.Configuracoes.Where(c => c.Ativo).ToListAsync(ct);
            _log.LogInformation("Prospecção iniciada: {N} configuração(ões) ativa(s)", configs.Count);

            foreach (var config in configs)
            {
                ct.ThrowIfCancellationRequested();
                totalNovos += await ProcessarConfiguracaoAsync(config, ct);
            }

            execucao.LeadsGerados = totalNovos;
            execucao.Status = StatusExecucao.Sucesso;
        }
        catch (OperationCanceledException)
        {
            execucao.Status = StatusExecucao.Erro;
            execucao.Erro = "Execução cancelada.";
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro na rotina de prospecção");
            execucao.Status = StatusExecucao.Erro;
            execucao.Erro = ex.Message;
        }
        finally
        {
            execucao.FinalizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Prospecção finalizada: {Status}, {N} novo(s) lead(s) pontuado(s)",
            execucao.Status, execucao.LeadsGerados);
        return execucao;
    }

    private async Task<int> ProcessarConfiguracaoAsync(ConfiguracaoProspeccao config, CancellationToken ct)
    {
        var ufs = SepararLista(config.Ufs).Select(u => u.ToUpperInvariant()).ToList();
        var cnaes = SepararLista(config.Cnaes).Select(SoDigitos).Where(c => c.Length > 0).ToList();

        // Filtro grosso no banco: situação ATIVA, UF/capital, e ainda não pontuado nesta config.
        var query = _db.Leads.Where(l =>
            l.Situacao.ToUpper().Contains("ATIVA") &&
            !l.Scores.Any(s => s.ConfiguracaoId == config.Id));

        if (ufs.Count > 0)
            query = query.Where(l => ufs.Contains(l.Uf.ToUpper()));
        if (config.CapitalMin is not null)
            query = query.Where(l => l.CapitalSocial >= config.CapitalMin);
        if (config.CapitalMax is not null)
            query = query.Where(l => l.CapitalSocial <= config.CapitalMax);

        // Traz um pouco mais que o lote para refinar o CNAE em memória (formatos variam).
        var candidatos = await query
            .OrderByDescending(l => l.CapitalSocial)
            .Take(_tamanhoLote * 3)
            .ToListAsync(ct);

        var selecionados = candidatos
            .Where(l => cnaes.Count == 0 || cnaes.Any(c => CnaeCombina(l.Cnae, c)))
            .Take(_tamanhoLote)
            .ToList();

        _log.LogInformation("Config {Id}: {N} candidato(s) real(is) para pontuar", config.Id, selecionados.Count);

        var novos = 0;
        foreach (var lead in selecionados)
        {
            ct.ThrowIfCancellationRequested();

            var resultado = await _ia.ClassificarAsync(lead, config, ct);

            _db.LeadScores.Add(new LeadScore
            {
                LeadId = lead.Id,
                ConfiguracaoId = config.Id,
                Score = resultado.Score,
                Racional = resultado.Racional,
                Fonte = FonteReceita,
                GeradoEm = DateTime.UtcNow
            });
            novos++;

            // Persiste em pequenos blocos para não perder trabalho em lotes longos.
            if (novos % 20 == 0)
                await _db.SaveChangesAsync(ct);
        }

        await _db.SaveChangesAsync(ct);
        return novos;
    }

    private static List<string> SepararLista(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string SoDigitos(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>
    /// Compara CNAE do lead (ex.: "2110600") com o do filtro (ex.: "21106"/"2110600"),
    /// ambos só em dígitos. Casa por igualdade ou por prefixo (filtro a nível de classe).
    /// </summary>
    private static bool CnaeCombina(string cnaeLead, string cnaeFiltroDigitos)
    {
        var lead = SoDigitos(cnaeLead);
        if (lead.Length == 0) return false;
        return lead == cnaeFiltroDigitos || lead.StartsWith(cnaeFiltroDigitos);
    }
}
