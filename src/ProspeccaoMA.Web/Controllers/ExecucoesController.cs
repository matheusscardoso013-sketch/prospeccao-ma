using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;

namespace ProspeccaoMA.Web.Controllers;

[Authorize]
public class ExecucoesController : Controller
{
    private readonly AppDbContext _db;
    public ExecucoesController(AppDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        var execs = await _db.ExecucoesJob
            .OrderByDescending(e => e.IniciadoEm)
            .Take(50)
            .ToListAsync();
        return View(execs);
    }
}
