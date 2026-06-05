using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Data;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly AppDbContext _db;

    public HomeController(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        var hoje = DateTime.UtcNow.Date;
        var vm = new DashboardViewModel
        {
            TotalLeads = await _db.Leads.CountAsync(),
            ConfiguracoesAtivas = await _db.ConfiguracoesProspeccao.CountAsync(c => c.Ativo),
            UltimaExecucao = await _db.ExecucoesJob
                .OrderByDescending(e => e.IniciadoEm)
                .FirstOrDefaultAsync(),
            LeadsHoje = await _db.LeadScores.CountAsync(s => s.GeradoEm >= hoje)
        };
        return View(vm);
    }

    [AllowAnonymous]
    public IActionResult Privacy() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
