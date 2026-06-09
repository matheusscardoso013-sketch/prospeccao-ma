using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;

namespace ProspeccaoMA.Web.Controllers;

[Authorize]
public class CompradorController : Controller
{
    private readonly AppDbContext _db;
    public CompradorController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index(string? busca)
    {
        var query = _db.Compradores.Include(c => c.Sinergias).Where(c => c.Ativo);

        if (!string.IsNullOrWhiteSpace(busca))
        {
            var b = busca.Trim();
            query = query.Where(c =>
                EF.Functions.ILike(c.Nome, $"%{b}%") ||
                EF.Functions.ILike(c.Tese, $"%{b}%") ||
                (c.Tags != null && EF.Functions.ILike(c.Tags, $"%{b}%")) ||
                (c.TipoEmpresa != null && EF.Functions.ILike(c.TipoEmpresa, $"%{b}%")));
        }

        ViewData["Busca"] = busca;
        ViewData["Total"] = await _db.Compradores.CountAsync(c => c.Ativo);
        var lista = await query.OrderBy(c => c.Nome).ToListAsync();
        return View(lista);
    }

    /// <summary>Alvos (leads) com maior sinergia para a tese deste comprador.</summary>
    public async Task<IActionResult> Alvos(int id)
    {
        var comprador = await _db.Compradores.FirstOrDefaultAsync(c => c.Id == id);
        if (comprador is null) return NotFound();

        var sinergias = await _db.SinergiasComprador
            .Include(s => s.Lead)
            .Where(s => s.CompradorId == id)
            .OrderByDescending(s => s.Score)
            .ToListAsync();

        ViewData["Comprador"] = comprador;
        return View(sinergias);
    }
}
