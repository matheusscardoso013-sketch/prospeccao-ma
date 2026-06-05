using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

public class HomeController : Controller
{
    private readonly ILogger<HomeController> _logger;

    public HomeController(ILogger<HomeController> logger)
    {
        _logger = logger;
    }

    public IActionResult Index()
    {
        // Ponto de entrada: usuário autenticado vai ao dashboard; senão, ao login.
        return User.Identity?.IsAuthenticated == true
            ? RedirectToAction("Index", "Lead")
            : RedirectToAction("Login", "Conta");
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
