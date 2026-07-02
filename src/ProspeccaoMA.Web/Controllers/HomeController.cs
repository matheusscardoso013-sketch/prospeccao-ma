using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Util;

namespace ProspeccaoMA.Web.Controllers;

public class HomeController : Controller
{
    private readonly AppDbContext _db;
    private readonly ILogger<HomeController> _logger;

    public HomeController(AppDbContext db, ILogger<HomeController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [Authorize]
    public async Task<IActionResult> Index()
    {
        var inicioHoje = Fuso.InicioHojeUtc();
        var hora = Fuso.Agora.Hour;

        // Contagens do pipeline (uma varredura), reaproveitadas nos KPIs e no funil.
        var porStatus = (await _db.SinergiasComprador
                .GroupBy(s => s.Status)
                .Select(g => new { g.Key, N = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.Key, x => x.N);
        int Cont(StatusSinergia s) => porStatus.TryGetValue(s, out var n) ? n : 0;

        var vm = new PainelVm
        {
            Saudacao = hora < 12 ? "Bom dia" : hora < 18 ? "Boa tarde" : "Boa noite",
            DataExtenso = Fuso.Agora.ToString("dddd, d 'de' MMMM"),
            AlvosNaBase = await _db.Leads.CountAsync(),
            MatchesNovosHoje = await _db.SinergiasComprador.CountAsync(s => s.GeradoEm >= inicioHoje),
            OportunidadesQuentes = await _db.SinergiasComprador
                .CountAsync(s => s.Score >= 80 && s.Status != StatusSinergia.Descartado),
            EmNegociacao = Cont(StatusSinergia.EmNegociacao),
        };

        // Funil: só as etapas ativas do pipeline (sem Descartado), na ordem.
        var etapas = new[]
        {
            (StatusSinergia.Novo,         "Novo",           "navy"),
            (StatusSinergia.Abordado,     "Abordado",       "azul"),
            (StatusSinergia.Reuniao,      "Reunião",        "azul"),
            (StatusSinergia.EmNegociacao, "Em negociação",  "ciano"),
        };
        var maior = etapas.Max(e => Cont(e.Item1));
        maior = Math.Max(maior, 1);
        vm.Funil = etapas.Select(e => new EtapaFunil
        {
            Rotulo = e.Item2,
            Cor = e.Item3,
            Total = Cont(e.Item1),
            Altura = 16 + 84.0 * Cont(e.Item1) / maior, // piso de 16% p/ a etapa não sumir
        }).ToList();

        // Melhores oportunidades em aberto (status Novo), maiores scores.
        var melhores = await _db.SinergiasComprador
            .Include(s => s.Lead).Include(s => s.Comprador)
            .Where(s => s.Status == StatusSinergia.Novo && s.Score >= 50)
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.GeradoEm)
            .Take(5)
            .ToListAsync();

        vm.Melhores = melhores.Select(s => new OportunidadePainel
        {
            LeadId = s.LeadId,
            Alvo = s.Lead?.RazaoSocial ?? "—",
            Setor = PrimeiroNaoVazio(s.Lead?.Segmento, Util.CnaeCatalogo.Rotulo(s.Lead?.Cnae, 30), "—"),
            Comprador = s.Comprador?.Nome ?? "—",
            Responsavel = s.Comprador?.Responsavel,
            Score = s.Score,
            Racional = Resumir(s.Racional),
        }).ToList();

        return View(vm);
    }

    private static string PrimeiroNaoVazio(params string?[] vals)
        => vals.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "—";

    /// <summary>Primeira frase do racional (antes do detalhamento "Setor n/..."), enxuta.</summary>
    private static string Resumir(string? racional)
    {
        if (string.IsNullOrWhiteSpace(racional)) return "";
        var t = racional.Trim();
        var corte = t.IndexOf(" Setor ", StringComparison.OrdinalIgnoreCase);
        if (corte > 30) t = t[..corte];
        return t.Length > 130 ? t[..127].TrimEnd() + "…" : t;
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
