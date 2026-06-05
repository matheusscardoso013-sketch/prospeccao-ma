using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Data;
using Prospeccao.Web.IA;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.Jobs;

/// <summary>
/// Uma execução da rotina de prospecção (seção 4 da spec). Para cada configuração
/// ativa: filtra leads reais pelo recorte (CNAE/UF/capital), pula os já pontuados,
/// pede à IA um score+racional e grava LeadScore. Registra tudo em ExecucaoJob.
/// Compartilhada pelo job das 12h e pelo botão "Rodar agora".
/// </summary>
public class RotinaProspeccao
{
    public const string Fonte = "Receita Federal — base pública";
    private const int LotePorConfig = 50;

    private readonly AppDbContext _db;
    private readonly IClassificadorIA _ia;
    private readonly ILogger<RotinaProspeccao> _log;

    public RotinaProspeccao(AppDbContext db, IClassificadorIA ia, ILogger<RotinaProspeccao> log)
    {
        _db = db;
        _ia = ia;
        _log = log;
    }

    public async Task<ExecucaoJob> ExecutarAsync(CancellationToken ct = default)
    {
        var execucao = new ExecucaoJob
        {
            IniciadoEm = DateTime.UtcNow,
            Status = "EmAndamento"
        };
        _db.ExecucoesJob.Add(execucao);
        await _db.SaveChangesAsync(ct);

        try
        {
            var configs = await _db.ConfiguracoesProspeccao
                .Where(c => c.Ativo)
                .ToListAsync(ct);

            var totalGerados = 0;
            foreach (var config in configs)
                totalGerados += await ProcessarConfigAsync(config, ct);

            execucao.LeadsGerados = totalGerados;
            execucao.Status = "Sucesso";
            execucao.FinalizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Rotina concluída: {Total} leads pontuados", totalGerados);
        }
        catch (Exception ex)
        {
            execucao.Status = "Erro";
            execucao.Erro = ex.Message;
            execucao.FinalizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync(CancellationToken.None);
            _log.LogError(ex, "Erro na rotina de prospecção");
        }

        return execucao;
    }

    private async Task<int> ProcessarConfigAsync(ConfiguracaoProspeccao config, CancellationToken ct)
    {
        var cnaes = SepararCsv(config.Cnaes);
        var ufs = SepararCsv(config.Ufs);
        if (cnaes.Count == 0 || ufs.Count == 0)
            return 0;

        // Candidatos: dentro do recorte e ainda NÃO pontuados para esta configuração.
        var query = _db.Leads
            .Where(l => l.Cnae != null && cnaes.Contains(l.Cnae))
            .Where(l => l.Uf != null && ufs.Contains(l.Uf));

        if (config.CapitalMin.HasValue)
            query = query.Where(l => l.CapitalSocial >= config.CapitalMin);
        if (config.CapitalMax.HasValue)
            query = query.Where(l => l.CapitalSocial <= config.CapitalMax);

        query = query
            .Where(l => !l.Scores.Any(s => s.ConfiguracaoId == config.Id))
            .OrderByDescending(l => l.Situacao == "ATIVA");

        var candidatos = await query.Take(LotePorConfig).ToListAsync(ct);

        var gerados = 0;
        foreach (var lead in candidatos)
        {
            ct.ThrowIfCancellationRequested();
            var r = await _ia.QualificarAsync(lead, config, ct);
            if (!r.Sucesso)
            {
                // Defensivo: não derruba o ciclo; será tentado de novo numa próxima execução.
                _log.LogWarning("Lead {Cnpj} não pontuado: {Motivo}", lead.Cnpj, r.Racional);
                continue;
            }

            _db.LeadScores.Add(new LeadScore
            {
                LeadId = lead.Id,
                ConfiguracaoId = config.Id,
                Score = r.Score,
                Racional = r.Racional,
                Fonte = Fonte,
                GeradoEm = DateTime.UtcNow
            });
            gerados++;
        }

        await _db.SaveChangesAsync(ct);
        return gerados;
    }

    private static List<string> SepararCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? new List<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
