using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

[Authorize]
public class CompradorController : Controller
{
    private readonly AppDbContext _db;
    private readonly ProspeccaoMA.Web.Matching.IMotorSinergia _motor;
    public CompradorController(AppDbContext db, ProspeccaoMA.Web.Matching.IMotorSinergia motor)
    {
        _db = db;
        _motor = motor;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recalcular(int id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var n = await _motor.RecalcularCompradorAsync(id, cts.Token);
        TempData["Ok"] = $"{n} sinergia(s) recalculada(s) com a tese atual.";
        return RedirectToAction(nameof(Alvos), new { id });
    }

    // Tese com menos de 20 caracteres é tratada como "sem tese" (vazia ou inservível p/ matching).
    private const int TamanhoMinimoTese = 20;

    public async Task<IActionResult> Index(string? busca, bool semTese = false)
    {
        var query = _db.Compradores.Include(c => c.Sinergias).Where(c => c.Ativo);

        if (semTese)
            query = query.Where(c => c.Tese.Length < TamanhoMinimoTese);

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
        ViewData["SemTese"] = semTese;
        ViewData["Total"] = await _db.Compradores.CountAsync(c => c.Ativo);
        ViewData["TotalSemTese"] = await _db.Compradores.CountAsync(c => c.Ativo && c.Tese.Length < TamanhoMinimoTese);
        var lista = await query.OrderBy(c => c.Nome).ToListAsync();
        return View(lista);
    }

    [HttpGet]
    public async Task<IActionResult> Editar(int id)
    {
        var c = await _db.Compradores.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        return View(c);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Salvar(Comprador modelo)
    {
        var c = await _db.Compradores.FirstOrDefaultAsync(x => x.Id == modelo.Id);
        if (c is null) return NotFound();

        if (string.IsNullOrWhiteSpace(modelo.Nome))
        {
            ModelState.AddModelError(string.Empty, "O nome é obrigatório.");
            return View("Editar", modelo);
        }

        c.Nome = modelo.Nome.Trim();
        c.RazaoSocial = modelo.RazaoSocial;
        c.Contato = modelo.Contato;
        c.Responsavel = modelo.Responsavel;
        c.TipoEmpresa = modelo.TipoEmpresa;
        c.Segmento = modelo.Segmento;
        c.Site = modelo.Site;
        c.FaixaFaturamento = modelo.FaixaFaturamento;
        c.Tags = modelo.Tags;
        c.Tese = modelo.Tese ?? string.Empty;
        c.FaturamentoMinAlvo = modelo.FaturamentoMinAlvo;
        c.FaturamentoMaxAlvo = modelo.FaturamentoMaxAlvo;
        c.MargemEbitdaMinima = modelo.MargemEbitdaMinima;
        c.TipoOperacao = modelo.TipoOperacao;
        c.GeografiaAlvo = modelo.GeografiaAlvo;
        c.ModeloNegocioAlvo = modelo.ModeloNegocioAlvo;
        c.Exclusoes = modelo.Exclusoes;
        c.Cultura = modelo.Cultura;
        c.Ativo = modelo.Ativo;

        try
        {
            await _db.SaveChangesAsync();
            TempData["Ok"] = "Comprador atualizado.";
            return RedirectToAction(nameof(Index));
        }
        catch (DbUpdateException)
        {
            ModelState.AddModelError(string.Empty, "Já existe um comprador com esse nome. Use um nome diferente.");
            return View("Editar", modelo);
        }
    }

    /// <summary>Exclui o comprador e seus cruzamentos. Obs.: um reimport da planilha buy-side
    /// pode recriá-lo (a exclusão não é uma lista de bloqueio).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Excluir(int id)
    {
        var c = await _db.Compradores.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        _db.Compradores.Remove(c); // SinergiasComprador caem em cascata
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Comprador \"{c.Nome}\" excluído (com seus cruzamentos).";
        return RedirectToAction(nameof(Index));
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
