using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

/// <summary>
/// Contas da equipe: cada pessoa entra com o próprio login (as anotações do mini-CRM
/// ficam assinadas por quem registrou). Qualquer usuário logado pode gerenciar — o
/// acesso à plataforma já é restrito ao time.
/// </summary>
[Authorize]
public class EquipeController : Controller
{
    private readonly UserManager<Usuario> _usuarios;
    public EquipeController(UserManager<Usuario> usuarios) => _usuarios = usuarios;

    public IActionResult Index()
    {
        var lista = _usuarios.Users.OrderBy(u => u.Email).ToList();
        return View(lista);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Criar(string email, string senha)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha))
        {
            TempData["Erro"] = "Informe e-mail e senha.";
            return RedirectToAction(nameof(Index));
        }

        var u = new Usuario { UserName = email.Trim(), Email = email.Trim(), EmailConfirmed = true };
        var r = await _usuarios.CreateAsync(u, senha);
        TempData[r.Succeeded ? "Ok" : "Erro"] = r.Succeeded
            ? $"Conta criada para {email}."
            : string.Join(" ", r.Errors.Select(e => e.Description));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Excluir(string id)
    {
        var u = await _usuarios.FindByIdAsync(id);
        if (u is null) return NotFound();
        if (string.Equals(u.Email, User.Identity?.Name, StringComparison.OrdinalIgnoreCase))
        {
            TempData["Erro"] = "Você não pode excluir a própria conta logada.";
            return RedirectToAction(nameof(Index));
        }

        await _usuarios.DeleteAsync(u);
        TempData["Ok"] = $"Conta {u.Email} removida.";
        return RedirectToAction(nameof(Index));
    }
}
