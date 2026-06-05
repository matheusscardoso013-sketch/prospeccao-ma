using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Jobs;

namespace MapeamentoMA.Web.Controllers;

[Authorize]
public class GatilhoController : Controller
{
    private readonly AppDbContext _db;
    private readonly CicloIngestaoService _ciclo;

    public GatilhoController(AppDbContext db, CicloIngestaoService ciclo)
    {
        _db = db;
        _ciclo = ciclo;
    }

    public async Task<IActionResult> Index(string? tipo, int? empresaId, bool? pendentes)
    {
        var query = _db.Gatilhos
            .Include(g => g.Empresa)
            .AsQueryable();

        if (!string.IsNullOrEmpty(tipo))
            query = query.Where(g => g.Tipo == tipo);
        if (empresaId.HasValue)
            query = query.Where(g => g.EmpresaId == empresaId);
        if (pendentes == true)
            query = query.Where(g => !g.Revisado);

        var gatilhos = await query
            .OrderByDescending(g => g.DataEvento)
            .Take(200)
            .ToListAsync();

        ViewBag.Tipos = new[] { "Sucessao", "Estresse", "Captacao", "Consolidacao", "Regulatorio" };
        return View(gatilhos);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarcarRevisado(int id)
    {
        var g = await _db.Gatilhos.FindAsync(id);
        if (g is null) return NotFound();
        g.Revisado = true;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = "Socio,Analista")]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecutarCiclo()
    {
        _ = Task.Run(() => _ciclo.ExecutarCicloAsync());
        TempData["Mensagem"] = "Ciclo de ingestão iniciado em background.";
        return RedirectToAction(nameof(Index));
    }
}
