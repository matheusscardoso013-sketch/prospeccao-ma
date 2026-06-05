using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

public class ContaController : Controller
{
    private readonly SignInManager<Usuario> _signIn;

    public ContaController(SignInManager<Usuario> signIn) => _signIn = signIn;

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string senha, string? returnUrl = null)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(senha))
        {
            ModelState.AddModelError(string.Empty, "Informe e-mail e senha.");
            return View();
        }

        var r = await _signIn.PasswordSignInAsync(email, senha, isPersistent: true, lockoutOnFailure: false);
        if (r.Succeeded)
            return LocalRedirect(returnUrl ?? Url.Action("Index", "Lead")!);

        ModelState.AddModelError(string.Empty, "E-mail ou senha inválidos.");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction("Login");
    }
}
