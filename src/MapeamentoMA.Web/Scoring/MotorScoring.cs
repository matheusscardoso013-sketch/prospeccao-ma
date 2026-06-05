using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Models;

namespace MapeamentoMA.Web.Scoring;

public class MotorScoring
{
    private readonly AppDbContext _db;
    private readonly ScoringConfig _cfg;

    public MotorScoring(AppDbContext db, IOptionsSnapshot<ScoringConfig> cfg)
    {
        _db = db;
        _cfg = cfg.Value;
    }

    public async Task<Score> CalcularAsync(int empresaId, int teseId, CancellationToken ct = default)
    {
        var empresa = await _db.Empresas
            .Include(e => e.Gatilhos)
            .FirstOrDefaultAsync(e => e.Id == empresaId, ct)
            ?? throw new InvalidOperationException($"Empresa {empresaId} não encontrada.");

        var tese = await _db.Teses.FindAsync([teseId], ct)
            ?? throw new InvalidOperationException($"Tese {teseId} não encontrada.");

        var fitTese        = CalcularFitTese(empresa, tese);
        var fitFaturamento = CalcularFitFaturamento(empresa, tese);
        var recencia       = CalcularRecencia(empresa);
        const double acesso = 0.5; // refinado na Fase 4

        var valor = 100.0 * (
            _cfg.PesoFitTese        * fitTese +
            _cfg.PesoFitFaturamento * fitFaturamento +
            _cfg.PesoRecencia       * recencia +
            _cfg.PesoAcesso         * acesso);

        var pesosJson = JsonSerializer.Serialize(new
        {
            PesoFitTese        = _cfg.PesoFitTese,
            PesoFitFaturamento = _cfg.PesoFitFaturamento,
            PesoRecencia       = _cfg.PesoRecencia,
            PesoAcesso         = _cfg.PesoAcesso,
            MeiaVidaRecenciaDias = _cfg.MeiaVidaRecenciaDias
        });

        // Atualiza ou cria snapshot
        var score = await _db.Scores
            .FirstOrDefaultAsync(s => s.EmpresaId == empresaId && s.TeseId == teseId, ct);

        if (score is null)
        {
            score = new Score { EmpresaId = empresaId, TeseId = teseId };
            _db.Scores.Add(score);
        }

        score.Valor                = (decimal)Math.Round(Math.Clamp(valor, 0, 100), 2);
        score.CompFitTese          = fitTese;
        score.CompFitFaturamento   = fitFaturamento;
        score.CompRecencia         = recencia;
        score.CompAcesso           = acesso;
        score.PesosJson            = pesosJson;
        score.CalculadoEm          = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return score;
    }

    // Recalcula score para todas as teses ativas vinculadas à empresa
    public async Task RecalcularEmpresaAsync(int empresaId, CancellationToken ct = default)
    {
        var empresa = await _db.Empresas.FindAsync([empresaId], ct);
        if (empresa is null) return;

        var teses = await _db.Teses.Where(t => t.Ativa).ToListAsync(ct);

        // Usa a tese vinculada se existir; caso contrário calcula para todas as ativas
        var tesesToCalc = empresa.TeseId.HasValue
            ? teses.Where(t => t.Id == empresa.TeseId.Value).ToList()
            : teses;

        foreach (var tese in tesesToCalc)
            await CalcularAsync(empresaId, tese.Id, ct);
    }

    private static double CalcularFitTese(Empresa empresa, Tese tese)
    {
        if (string.IsNullOrWhiteSpace(empresa.CnaePrincipal) ||
            string.IsNullOrWhiteSpace(tese.CnaesAlvo))
            return 0;

        var cnaes = tese.CnaesAlvo.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        // Match exato
        if (cnaes.Contains(empresa.CnaePrincipal))
            return 1.0;

        // Mesma divisão (primeiros 2 dígitos)
        var divEmpresa = empresa.CnaePrincipal.Length >= 2 ? empresa.CnaePrincipal[..2] : "";
        if (cnaes.Any(c => c.Length >= 2 && c[..2] == divEmpresa))
            return 0.5;

        return 0.0;
    }

    private static double CalcularFitFaturamento(Empresa empresa, Tese tese)
    {
        if (empresa.FaturamentoEstimado is null || tese.FaturamentoMax <= 0)
            return 0.5; // sem dados: neutro

        var fat = (double)empresa.FaturamentoEstimado.Value;
        var min = (double)tese.FaturamentoMin;
        var max = (double)tese.FaturamentoMax;

        if (fat >= min && fat <= max) return 1.0;

        // Decaimento linear: quanto mais distante da faixa, menor o fit
        var distancia = fat < min ? min - fat : fat - max;
        var faixa = max - min;
        if (faixa <= 0) return 0;

        return Math.Max(0, 1.0 - distancia / faixa);
    }

    private double CalcularRecencia(Empresa empresa)
    {
        var gatilho = empresa.Gatilhos
            .OrderByDescending(g => g.DataEvento)
            .FirstOrDefault();

        if (gatilho is null) return 0;

        var dias = (DateTime.Today - gatilho.DataEvento.ToDateTime(TimeOnly.MinValue)).TotalDays;
        var recencia = Math.Exp(-Math.Log(2) * dias / _cfg.MeiaVidaRecenciaDias);
        return recencia * gatilho.Confianca;
    }
}
