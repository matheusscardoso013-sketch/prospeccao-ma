using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Data;
using Prospeccao.Web.Ingestao;
using Prospeccao.Web.Jobs;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.Controllers;

/// <summary>Listagem de leads pontuados, com filtros e ordenação por score.</summary>
[Authorize]
public class LeadController : Controller
{
    private readonly AppDbContext _db;
    private readonly ImportadorCnpj _importador;
    private readonly RotinaProspeccao _rotina;

    public LeadController(AppDbContext db, ImportadorCnpj importador, RotinaProspeccao rotina)
    {
        _db = db;
        _importador = importador;
        _rotina = rotina;
    }

    public async Task<IActionResult> Index(string? uf, string? situacao, string ordenacao = "score")
    {
        var query = _db.Leads.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(uf))
            query = query.Where(l => l.Uf == uf);
        if (!string.IsNullOrWhiteSpace(situacao))
            query = query.Where(l => l.Situacao == situacao);

        // Projeta cada lead com o seu melhor score (maior nota) e o racional correspondente.
        var dados = await query
            .Select(l => new
            {
                Lead = l,
                Melhor = l.Scores
                    .OrderByDescending(s => s.Score)
                    .Select(s => new { s.Score, s.Racional, s.Fonte, s.GeradoEm })
                    .FirstOrDefault()
            })
            .ToListAsync();

        var linhas = dados.Select(d => new LeadLinha
        {
            Lead = d.Lead,
            MelhorScore = d.Melhor == null ? (int?)null : d.Melhor.Score,
            Racional = d.Melhor?.Racional,
            Fonte = d.Melhor?.Fonte,
            GeradoEm = d.Melhor?.GeradoEm
        });

        linhas = ordenacao == "razao"
            ? linhas.OrderBy(x => x.Lead.RazaoSocial)
            : linhas.OrderByDescending(x => x.MelhorScore ?? -1).ThenBy(x => x.Lead.RazaoSocial);

        return View(new LeadListaViewModel
        {
            Linhas = linhas.ToList(),
            Uf = uf,
            Situacao = situacao,
            Ordenacao = ordenacao
        });
    }

    // --- Importação de CNPJs reais (enriquecidos via BrasilAPI) ---

    [HttpGet]
    public IActionResult Importar() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Importar(string? cnpjs, IFormFile? arquivo, CancellationToken ct)
    {
        var conteudo = cnpjs ?? string.Empty;
        if (arquivo is { Length: > 0 })
        {
            using var reader = new StreamReader(arquivo.OpenReadStream());
            conteudo += "\n" + await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(conteudo))
        {
            TempData["Erro"] = "Cole ao menos um CNPJ ou envie um arquivo.";
            return RedirectToAction(nameof(Importar));
        }

        var r = await _importador.ImportarAsync(conteudo, ct);
        TempData["Mensagem"] =
            $"Importação: {r.Importados} novos, {r.JaExistiam} já existiam, " +
            $"{r.NaoEncontrados} não encontrados, {r.Invalidos} inválidos.";
        return RedirectToAction(nameof(Index));
    }

    // --- Disparo manual da rotina de pontuação (além do job das 12h) ---

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RodarAgora(CancellationToken ct)
    {
        var exec = await _rotina.ExecutarAsync(ct);
        TempData["Mensagem"] = exec.Status == "Sucesso"
            ? $"Rotina concluída: {exec.LeadsGerados} lead(s) pontuado(s)."
            : $"Rotina terminou com status '{exec.Status}'. {exec.Erro}";
        return RedirectToAction(nameof(Index));
    }
}
