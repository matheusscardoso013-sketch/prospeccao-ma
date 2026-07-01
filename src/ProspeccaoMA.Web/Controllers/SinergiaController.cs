using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

/// <summary>Atualização de status/anotações de um match (pipeline de trabalho do time).</summary>
[Authorize]
public class SinergiaController : Controller
{
    private readonly AppDbContext _db;
    public SinergiaController(AppDbContext db) => _db = db;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Atualizar(int id, StatusSinergia status, string? anotacoes, string? voltar)
    {
        var s = await _db.SinergiasComprador.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();

        s.Status = status;
        s.Anotacoes = string.IsNullOrWhiteSpace(anotacoes) ? null : anotacoes.Trim();
        s.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Ok"] = "Match atualizado.";
        return LocalRedirect(string.IsNullOrWhiteSpace(voltar) ? "/Mesa" : voltar);
    }

    /// <summary>Move um match para outro status (usado pelo arrastar-e-soltar do quadro Kanban).
    /// Só altera o status; responde JSON sem redirecionar.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mover(int id, StatusSinergia status)
    {
        var s = await _db.SinergiasComprador.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();

        s.Status = status;
        s.AtualizadoEm = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }
}
