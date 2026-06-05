using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Models;

namespace MapeamentoMA.Web.Controllers;

[Authorize]
public class PipelineController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<IdentityUser> _users;

    public PipelineController(AppDbContext db, UserManager<IdentityUser> users)
    {
        _db = db;
        _users = users;
    }

    public async Task<IActionResult> Index()
    {
        var estagios = new[] { "Identificado", "Qualificado", "Abordagem", "Conversa", "Mandato", "Descartado" };

        ViewBag.UltimaExecucao = await _db.JobExecucoes
            .Where(j => j.NomeJob == "CicloIngestao")
            .OrderByDescending(j => j.IniciadoEm)
            .FirstOrDefaultAsync();
        var items = await _db.PipelineItems
            .Include(p => p.Empresa)
            .ThenInclude(e => e.Tese)
            .Include(p => p.Empresa)
            .ThenInclude(e => e.Gatilhos)
            .Include(p => p.Empresa)
            .ThenInclude(e => e.Scores)
            .ToListAsync();

        var kanban = estagios.ToDictionary(
            e => e,
            // Dentro de cada coluna: ordena por score decrescente, depois pela ordem manual
            e => items
                .Where(i => i.Estagio == e)
                .OrderByDescending(i => i.Empresa.Scores.FirstOrDefault()?.Valor ?? 0)
                .ThenBy(i => i.Ordem)
                .ToList());

        return View(kanban);
    }

    [HttpPost]
    public async Task<IActionResult> Mover([FromBody] MoverRequest req)
    {
        var item = await _db.PipelineItems.FindAsync(req.ItemId);
        if (item is null) return NotFound();

        item.Estagio = req.NovoEstagio;
        item.Ordem = req.NovaOrdem;
        item.AtualizadoEm = DateTime.UtcNow;
        item.AtualizadoPorUsuarioId = _users.GetUserId(User);

        await _db.SaveChangesAsync();
        return Ok();
    }
}

public record MoverRequest(int ItemId, string NovoEstagio, int NovaOrdem);
