using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Util;

namespace ProspeccaoMA.Web.Controllers;

/// <summary>
/// Mesa de operações: os melhores pares alvo × comprador em aberto, ordenados por score,
/// com responsável, status e anotações — a tela de trabalho diária do time.
/// </summary>
[Authorize]
public class MesaController : Controller
{
    private readonly AppDbContext _db;
    public MesaController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(StatusSinergia? status, int? scoreMin, string? busca, string? resp)
    {
        // Padrão: foco no que presta (score >= 50). "Qualquer" chega como 0 explícito.
        scoreMin ??= 50;

        var q = _db.SinergiasComprador
            .Include(s => s.Lead)
            .Include(s => s.Comprador)
            .AsQueryable();

        // Padrão: pares em aberto (tudo menos Descartado).
        q = status is null
            ? q.Where(s => s.Status != StatusSinergia.Descartado)
            : q.Where(s => s.Status == status);

        if (scoreMin > 0)
            q = q.Where(s => s.Score >= scoreMin);

        if (!string.IsNullOrWhiteSpace(resp))
            q = q.Where(s => s.Comprador!.Responsavel == resp);

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim();
            q = q.Where(s =>
                EF.Functions.ILike(s.Lead!.RazaoSocial, $"%{b}%") ||
                EF.Functions.ILike(s.Comprador!.Nome, $"%{b}%") ||
                (s.Comprador!.Responsavel != null && EF.Functions.ILike(s.Comprador.Responsavel, $"%{b}%")));
        }

        var linhas = await q
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.AtualizadoEm ?? s.GeradoEm)
            .Take(200)
            .ToListAsync();

        // KPIs do pipeline (respeitam o recorte por responsável, quando escolhido).
        // Só pares AVALIADOS: os de score 0 são fila da IA, não oportunidade (ver Util.Sinergias).
        var qKpi = _db.SinergiasComprador.AsQueryable();
        if (!string.IsNullOrWhiteSpace(resp))
            qKpi = qKpi.Where(s => s.Comprador!.Responsavel == resp);
        var contagens = await qKpi.Avaliadas()
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync();
        ViewData["Kpis"] = contagens.ToDictionary(x => x.Status, x => x.N);
        ViewData["Aguardando"] = await qKpi.NaoAvaliadas().CountAsync();

        ViewData["Status"] = status;
        ViewData["ScoreMin"] = scoreMin;
        ViewData["Busca"] = busca;
        ViewData["Resp"] = resp;
        ViewData["Responsaveis"] = await ResponsaveisAsync();
        return View(linhas);
    }

    private Task<List<string>> ResponsaveisAsync() => _db.Compradores
        .Where(c => c.Responsavel != null && c.Responsavel != "")
        .Select(c => c.Responsavel!).Distinct().OrderBy(r => r).ToListAsync();

    /// <summary>
    /// Auditoria da qualidade do matching: distribuição de scores, calibração por
    /// profundidade da tese e concentração por comprador. Serve para responder "a IA está
    /// funcionando?" com dado, não com impressão.
    /// </summary>
    public async Task<IActionResult> Qualidade(CancellationToken ct)
    {
        var vm = new QualidadeVm
        {
            TotalPares = await _db.SinergiasComprador.CountAsync(ct),
            PareAvaliados = await _db.SinergiasComprador.Avaliadas().CountAsync(ct),
            AguardandoAvaliacao = await _db.SinergiasComprador.NaoAvaliadas().CountAsync(ct)
        };

        (string Rotulo, int Min, int Max)[] faixas =
        {
            ("90-100 · excelente", 90, 100), ("80-89 · quente", 80, 89),
            ("60-79 · morno", 60, 79),       ("40-59 · fraco", 40, 59),
            ("1-39 · descartado", 1, 39)
        };
        foreach (var f in faixas)
        {
            var n = await _db.SinergiasComprador.CountAsync(s => s.Score >= f.Min && s.Score <= f.Max, ct);
            vm.Distribuicao.Add(new FaixaScore
            {
                Rotulo = f.Rotulo,
                Qtd = n,
                Pct = vm.PareAvaliados == 0 ? 0 : 100.0 * n / vm.PareAvaliados
            });
        }

        // Uma varredura só dos pares avaliados, com o tamanho da tese do comprador junto.
        var pares = await _db.SinergiasComprador.Avaliadas()
            .Select(s => new { s.Score, s.CompradorId, Nome = s.Comprador!.Nome, Tam = s.Comprador.Tese.Length })
            .ToListAsync(ct);

        (string Rotulo, int Min, int Max)[] bandas =
        {
            ("Sem tese (perfil do site)", 0, 19), ("Rasa · 20-99", 20, 99),
            ("Média · 100-299", 100, 299),        ("Detalhada · 300-999", 300, 999),
            ("Profunda · 1000+", 1000, int.MaxValue)
        };
        foreach (var b in bandas)
        {
            var doGrupo = pares.Where(p => p.Tam >= b.Min && p.Tam <= b.Max).ToList();
            if (doGrupo.Count == 0) continue;
            vm.PorProfundidadeTese.Add(new FaixaTese
            {
                Rotulo = b.Rotulo,
                Compradores = doGrupo.Select(p => p.CompradorId).Distinct().Count(),
                Pares = doGrupo.Count,
                ScoreMedio = doGrupo.Average(p => p.Score),
                PctQuentes = 100.0 * doGrupo.Count(p => p.Score >= 80) / doGrupo.Count
            });
        }

        // Desempenho por modelo da rotação. Só olha o que foi carimbado (pares antigos, de
        // antes do carimbo, ficam de fora e aparecem contados à parte).
        var comModelo = await _db.SinergiasComprador.Avaliadas()
            .Where(s => s.ModeloIA != null)
            .Select(s => new { s.ModeloIA, s.Score, Tam = s.Racional.Length, TemSub = s.ScoreSetor != null })
            .ToListAsync(ct);

        vm.SemModeloRegistrado = vm.PareAvaliados - comModelo.Count;
        vm.PorModelo = comModelo.GroupBy(x => x.ModeloIA!)
            .Select(g => new DesempenhoModelo
            {
                Modelo = g.Key,
                Pares = g.Count(),
                ScoreMedio = g.Average(x => x.Score),
                PctQuentes = 100.0 * g.Count(x => x.Score >= 80) / g.Count(),
                RacionalMedio = (int)g.Average(x => x.Tam),
                PctComSubscores = 100.0 * g.Count(x => x.TemSub) / g.Count()
            })
            .OrderByDescending(m => m.Pares)
            .ToList();

        vm.Concentracao = pares.GroupBy(p => p.CompradorId)
            .Select(g => new ConcentracaoComprador
            {
                Nome = g.First().Nome,
                TamanhoTese = g.First().Tam,
                Quentes = g.Count(p => p.Score >= 80),
                ScoreMedio = g.Average(p => p.Score)
            })
            .Where(c => c.Quentes > 0)
            .OrderByDescending(c => c.Quentes).ThenByDescending(c => c.ScoreMedio)
            .Take(12).ToList();

        return View(vm);
    }

    /// <summary>Vista em quadro (Kanban): uma coluna por status, arrastar-e-soltar move o match.</summary>
    public async Task<IActionResult> Kanban(int? scoreMin, string? busca, string? resp)
    {
        scoreMin ??= 50; // mesmo padrão da tabela: foco no que presta
        var vm = new KanbanVm { ScoreMin = scoreMin, Busca = busca, Resp = resp, Responsaveis = await ResponsaveisAsync() };
        var b = busca?.Trim();

        foreach (var st in Util.StatusUi.Todos)
        {
            var q = _db.SinergiasComprador
                .Include(s => s.Lead).Include(s => s.Comprador)
                .Where(s => s.Status == st);

            if (scoreMin > 0) q = q.Where(s => s.Score >= scoreMin);
            if (!string.IsNullOrWhiteSpace(resp)) q = q.Where(s => s.Comprador!.Responsavel == resp);
            if (!string.IsNullOrWhiteSpace(b))
                q = q.Where(s => EF.Functions.ILike(s.Lead!.RazaoSocial, $"%{b}%")
                              || EF.Functions.ILike(s.Comprador!.Nome, $"%{b}%"));

            var total = await q.CountAsync();
            var cards = await q.OrderByDescending(s => s.Score).ThenByDescending(s => s.AtualizadoEm ?? s.GeradoEm)
                .Take(40).ToListAsync();

            vm.Colunas.Add(new KanbanColuna
            {
                Status = st,
                Rotulo = Util.StatusUi.Rotulo(st),
                Css = Util.StatusUi.Css(st),
                Total = total,
                Cards = cards
            });
        }
        return View(vm);
    }
}
