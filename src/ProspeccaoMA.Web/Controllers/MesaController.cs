using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

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
        var qKpi = _db.SinergiasComprador.AsQueryable();
        if (!string.IsNullOrWhiteSpace(resp))
            qKpi = qKpi.Where(s => s.Comprador!.Responsavel == resp);
        var contagens = await qKpi
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync();
        ViewData["Kpis"] = contagens.ToDictionary(x => x.Status, x => x.N);

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
