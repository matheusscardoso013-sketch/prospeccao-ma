using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Models;

namespace MapeamentoMA.Web.Controllers;

[Authorize]
public class TeseController : Controller
{
    private readonly AppDbContext _db;

    public TeseController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
        => View(await _db.Teses.OrderBy(t => t.Nome).ToListAsync());

    public IActionResult Criar() => View(new Tese());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Criar(Tese tese)
    {
        if (!ModelState.IsValid) return View(tese);
        _db.Teses.Add(tese);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Editar(int id)
    {
        var tese = await _db.Teses.FindAsync(id);
        if (tese is null) return NotFound();
        return View(tese);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Editar(int id, Tese tese)
    {
        if (id != tese.Id) return BadRequest();
        if (!ModelState.IsValid) return View(tese);
        _db.Teses.Update(tese);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = "Socio")]
    public async Task<IActionResult> Desativar(int id)
    {
        var tese = await _db.Teses.FindAsync(id);
        if (tese is null) return NotFound();
        tese.Ativa = !tese.Ativa;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
