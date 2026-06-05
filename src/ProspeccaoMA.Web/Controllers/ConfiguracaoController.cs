using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

[Authorize]
public class ConfiguracaoController : Controller
{
    private readonly AppDbContext _db;
    private readonly UserManager<Usuario> _userManager;

    public ConfiguracaoController(AppDbContext db, UserManager<Usuario> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    private string Uid => _userManager.GetUserId(User)!;

    public async Task<IActionResult> Index()
    {
        var configs = await _db.Configuracoes
            .Where(c => c.UsuarioId == Uid)
            .OrderByDescending(c => c.CriadoEm)
            .ToListAsync();
        return View(configs);
    }

    [HttpGet]
    public IActionResult Editar(int? id)
    {
        if (id is null)
            return View(new ConfiguracaoProspeccao { Ativo = true });

        var cfg = _db.Configuracoes.FirstOrDefault(c => c.Id == id && c.UsuarioId == Uid);
        if (cfg is null) return NotFound();
        return View(cfg);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Salvar(ConfiguracaoProspeccao modelo)
    {
        if (string.IsNullOrWhiteSpace(modelo.Cnaes) || string.IsNullOrWhiteSpace(modelo.Ufs))
        {
            ModelState.AddModelError(string.Empty, "Informe ao menos um CNAE e uma UF.");
            return View("Editar", modelo);
        }

        if (modelo.Id == 0)
        {
            modelo.UsuarioId = Uid;
            modelo.CriadoEm = DateTime.UtcNow;
            _db.Configuracoes.Add(modelo);
        }
        else
        {
            var cfg = await _db.Configuracoes.FirstOrDefaultAsync(c => c.Id == modelo.Id && c.UsuarioId == Uid);
            if (cfg is null) return NotFound();
            cfg.Cnaes = modelo.Cnaes;
            cfg.Ufs = modelo.Ufs;
            cfg.CapitalMin = modelo.CapitalMin;
            cfg.CapitalMax = modelo.CapitalMax;
            cfg.Ativo = modelo.Ativo;
        }

        await _db.SaveChangesAsync();
        TempData["Ok"] = "Configuração salva.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AlternarAtivo(int id)
    {
        var cfg = await _db.Configuracoes.FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == Uid);
        if (cfg is null) return NotFound();
        cfg.Ativo = !cfg.Ativo;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Excluir(int id)
    {
        var cfg = await _db.Configuracoes.FirstOrDefaultAsync(c => c.Id == id && c.UsuarioId == Uid);
        if (cfg is null) return NotFound();
        _db.Configuracoes.Remove(cfg);
        await _db.SaveChangesAsync();
        TempData["Ok"] = "Configuração removida.";
        return RedirectToAction(nameof(Index));
    }
}
