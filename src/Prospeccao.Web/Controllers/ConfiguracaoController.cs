using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Data;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.Controllers;

/// <summary>CRUD das configurações de prospecção (setores/CNAE, UF, faixa de capital) do usuário.</summary>
[Authorize]
public class ConfiguracaoController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<IdentityUser> _userManager;

    public ConfiguracaoController(AppDbContext db, UserManager<IdentityUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    private string UsuarioId => _userManager.GetUserId(User)!;

    public async Task<IActionResult> Index()
    {
        var itens = await _db.ConfiguracoesProspeccao
            .Where(c => c.UsuarioId == UsuarioId)
            .OrderByDescending(c => c.Id)
            .ToListAsync();
        return View(itens);
    }

    public IActionResult Criar() => View(new ConfiguracaoProspeccao());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Criar(ConfiguracaoProspeccao model)
    {
        if (!ValidarFaixaCapital(model)) { /* erro já adicionado */ }
        if (!ModelState.IsValid) return View(model);

        model.UsuarioId = UsuarioId;
        _db.Add(model);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Editar(int id)
    {
        var model = await BuscarDoUsuarioAsync(id);
        if (model is null) return NotFound();
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Editar(int id, ConfiguracaoProspeccao model)
    {
        if (id != model.Id) return BadRequest();

        var existente = await BuscarDoUsuarioAsync(id);
        if (existente is null) return NotFound();

        ValidarFaixaCapital(model);
        if (!ModelState.IsValid) return View(model);

        existente.Cnaes = model.Cnaes;
        existente.Ufs = model.Ufs;
        existente.CapitalMin = model.CapitalMin;
        existente.CapitalMax = model.CapitalMax;
        existente.Ativo = model.Ativo;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Excluir(int id)
    {
        var model = await BuscarDoUsuarioAsync(id);
        if (model is null) return NotFound();
        _db.Remove(model);
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private Task<ConfiguracaoProspeccao?> BuscarDoUsuarioAsync(int id) =>
        _db.ConfiguracoesProspeccao
            .FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == UsuarioId);

    /// <summary>Garante CapitalMin &lt;= CapitalMax quando ambos informados.</summary>
    private bool ValidarFaixaCapital(ConfiguracaoProspeccao model)
    {
        if (model.CapitalMin.HasValue && model.CapitalMax.HasValue &&
            model.CapitalMin > model.CapitalMax)
        {
            ModelState.AddModelError(nameof(model.CapitalMax),
                "O capital máximo não pode ser menor que o mínimo.");
            return false;
        }
        return true;
    }
}
