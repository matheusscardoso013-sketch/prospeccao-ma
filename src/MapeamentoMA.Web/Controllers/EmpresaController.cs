using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Models;
using MapeamentoMA.Web.Ingestao;
using MapeamentoMA.Web.Scoring;

namespace MapeamentoMA.Web.Controllers;

[Authorize]
public class EmpresaController : Controller
{
    private readonly AppDbContext _db;
    private readonly MotorScoring _scoring;
    private readonly ConectorBrasilApi _brasilApi;

    public EmpresaController(AppDbContext db, MotorScoring scoring, ConectorBrasilApi brasilApi)
    {
        _db = db;
        _scoring = scoring;
        _brasilApi = brasilApi;
    }

    public async Task<IActionResult> Index(string? cnae, int? teseId)
    {
        var query = _db.Empresas.Include(e => e.Tese).Include(e => e.Scores).AsQueryable();
        if (!string.IsNullOrWhiteSpace(cnae))
            query = query.Where(e => e.CnaePrincipal == cnae);
        if (teseId.HasValue)
            query = query.Where(e => e.TeseId == teseId);

        ViewBag.Teses = await _db.Teses.Where(t => t.Ativa).ToListAsync();
        return View(await query
            .OrderByDescending(e => e.Scores.Max(s => (decimal?)s.Valor) ?? 0)
            .ThenBy(e => e.RazaoSocial)
            .ToListAsync());
    }

    public async Task<IActionResult> Importar()
    {
        ViewBag.Teses = await _db.Teses.Where(t => t.Ativa).ToListAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Importar(string cnpjs, int? teseId, string fonte)
    {
        if (string.IsNullOrWhiteSpace(cnpjs))
        {
            ModelState.AddModelError("", "Informe ao menos um CNPJ.");
            ViewBag.Teses = await _db.Teses.Where(t => t.Ativa).ToListAsync();
            return View();
        }

        var linhas = cnpjs.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        int importadas = 0, ignoradas = 0;

        foreach (var linha in linhas)
        {
            var cnpj = new string(linha.Trim().Where(char.IsDigit).ToArray());
            if (cnpj.Length != 14) { ignoradas++; continue; }

            var existe = await _db.Empresas.AnyAsync(e => e.Cnpj == cnpj);
            if (existe) { ignoradas++; continue; }

            var empresa = new Empresa
            {
                Cnpj = cnpj,
                RazaoSocial = $"CNPJ {cnpj}",
                TeseId = teseId,
                FonteOrigem = string.IsNullOrWhiteSpace(fonte) ? "Importação manual" : fonte,
                DataColeta = DateTime.UtcNow
            };
            // Enriquece com dados reais da BrasilAPI antes de salvar
            await _brasilApi.EnriquecerAsync(empresa);
            _db.Empresas.Add(empresa);
            await _db.SaveChangesAsync();

            var item = new PipelineItem
            {
                EmpresaId = empresa.Id,
                Estagio = "Identificado",
                Ordem = await _db.PipelineItems.CountAsync(p => p.Estagio == "Identificado"),
                AtualizadoEm = DateTime.UtcNow
            };
            _db.PipelineItems.Add(item);
            await _db.SaveChangesAsync();

            await _scoring.RecalcularEmpresaAsync(empresa.Id);

            importadas++;
        }

        TempData["Mensagem"] = $"{importadas} empresa(s) importada(s). {ignoradas} ignorada(s) (duplicadas ou CNPJ inválido).";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Detalhe(int id)
    {
        var empresa = await _db.Empresas
            .Include(e => e.Tese)
            .Include(e => e.Gatilhos)
            .Include(e => e.Scores)
            .Include(e => e.PipelineItem)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (empresa is null) return NotFound();
        return View(empresa);
    }
}
