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

    public async Task<IActionResult> Index(StatusSinergia? status, int? scoreMin, string? busca)
    {
        var q = _db.SinergiasComprador
            .Include(s => s.Lead)
            .Include(s => s.Comprador)
            .AsQueryable();

        // Padrão: pares em aberto (tudo menos Descartado).
        q = status is null
            ? q.Where(s => s.Status != StatusSinergia.Descartado)
            : q.Where(s => s.Status == status);

        if (scoreMin is not null)
            q = q.Where(s => s.Score >= scoreMin);

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

        // KPIs do pipeline.
        var contagens = await _db.SinergiasComprador
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, N = g.Count() })
            .ToListAsync();
        ViewData["Kpis"] = contagens.ToDictionary(x => x.Status, x => x.N);

        ViewData["Status"] = status;
        ViewData["ScoreMin"] = scoreMin;
        ViewData["Busca"] = busca;
        return View(linhas);
    }
}
