using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Ingestao;
using ProspeccaoMA.Web.Jobs;
using ProspeccaoMA.Web.Models.ViewModels;

namespace ProspeccaoMA.Web.Controllers;

[Authorize]
public class LeadController : Controller
{
    private readonly AppDbContext _db;
    private readonly IImportadorCnpj _importador;
    private readonly RotinaProspeccao _rotina;
    private readonly ProspeccaoMA.Web.Matching.IMotorSinergia _motor;
    private readonly ILogger<LeadController> _log;

    public LeadController(AppDbContext db, IImportadorCnpj importador, RotinaProspeccao rotina,
        ProspeccaoMA.Web.Matching.IMotorSinergia motor, ILogger<LeadController> log)
    {
        _db = db;
        _importador = importador;
        _rotina = rotina;
        _motor = motor;
        _log = log;
    }

    /// <summary>Mostra os compradores com maior sinergia para um lead (calcula on-demand se ainda não houver).</summary>
    public async Task<IActionResult> Compradores(int id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();

        if (!await _db.SinergiasComprador.AnyAsync(s => s.LeadId == id))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await _motor.CruzarLeadAsync(lead, cts.Token);
        }

        var sinergias = await _db.SinergiasComprador
            .Include(s => s.Comprador)
            .Where(s => s.LeadId == id)
            .OrderByDescending(s => s.Score)
            .ToListAsync();

        ViewData["Lead"] = lead;
        return View(sinergias);
    }

    public async Task<IActionResult> Index(string? cnae, string? uf, int? scoreMin, OrdenacaoLeads ordenar = OrdenacaoLeads.Score)
    {
        var linhas = await CarregarLinhasAsync(cnae, uf, scoreMin, ordenar);

        var vm = new LeadsViewModel
        {
            Cnae = cnae,
            Uf = uf,
            ScoreMin = scoreMin,
            Ordenar = ordenar,
            Leads = linhas,
            TotalLeads = await _db.Leads.CountAsync(),
            GeradosHoje = await _db.LeadScores.CountAsync(s => s.GeradoEm.Date == DateTime.UtcNow.Date),
            ScoreMedio = linhas.Count > 0 ? (int)Math.Round(linhas.Average(l => l.Score)) : 0,
            UltimaExecucao = await _db.ExecucoesJob.OrderByDescending(e => e.IniciadoEm).FirstOrDefaultAsync(),
            Ufs = await _db.Leads.Where(l => l.Uf != "").Select(l => l.Uf).Distinct().OrderBy(u => u).ToListAsync(),
            Cnaes = await _db.Leads.Where(l => l.Cnae != "").Select(l => l.Cnae).Distinct().OrderBy(c => c).ToListAsync()
        };
        return View(vm);
    }

    /// <summary>Monta as linhas (lead + melhor score) aplicando os filtros. Reusado pelo export.</summary>
    private async Task<List<LeadLinha>> CarregarLinhasAsync(string? cnae, string? uf, int? scoreMin, OrdenacaoLeads ordenar)
    {
        // Só exibimos leads que já foram pontuados pela IA.
        var query = _db.Leads.Include(l => l.Scores).Where(l => l.Scores.Any());

        if (!string.IsNullOrWhiteSpace(uf))
            query = query.Where(l => l.Uf == uf);
        if (!string.IsNullOrWhiteSpace(cnae))
            query = query.Where(l => l.Cnae == cnae);

        var leads = await query.ToListAsync();

        var linhas = leads.Select(l =>
        {
            var melhor = l.Scores.OrderByDescending(s => s.Score).First();
            return new LeadLinha(l, melhor.Score, melhor.Racional, melhor.Fonte, melhor.GeradoEm);
        });

        if (scoreMin is not null)
            linhas = linhas.Where(x => x.Score >= scoreMin);

        linhas = ordenar switch
        {
            OrdenacaoLeads.Capital => linhas.OrderByDescending(x => x.Lead.CapitalSocial),
            OrdenacaoLeads.Recente => linhas.OrderByDescending(x => x.GeradoEm),
            _ => linhas.OrderByDescending(x => x.Score)
        };

        return linhas.ToList();
    }

    [HttpGet]
    public IActionResult Importar() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Importar(string cnpjs)
    {
        var lista = (cnpjs ?? string.Empty)
            .Split(new[] { '\n', '\r', ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lista.Length == 0)
        {
            TempData["Erro"] = "Cole ao menos um CNPJ para importar.";
            return View();
        }

        // Operação longa (chamadas externas com rate limit): não atrelar ao RequestAborted,
        // senão o usuário sair da página abortaria a importação no meio.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var r = await _importador.ImportarAsync(lista, cts.Token);
        TempData["Ok"] = $"Importação: {r.Novos} novo(s), {r.Atualizados} atualizado(s), {r.Falhas} falha(s) de {r.Validos} CNPJ(s) válido(s).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RodarAgora()
    {
        // Idem: a rotina pode demorar (várias chamadas à IA). Desacoplada do RequestAborted.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var exec = await _rotina.ExecutarAsync(cts.Token);
        TempData["Ok"] = $"Rotina executada: {exec.Status}, {exec.LeadsGerados} lead(s) pontuado(s).";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Exportar(string? cnae, string? uf, int? scoreMin, OrdenacaoLeads ordenar = OrdenacaoLeads.Score)
    {
        var linhas = await CarregarLinhasAsync(cnae, uf, scoreMin, ordenar);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Leads");

        string[] cabec = { "Score", "Razão social", "CNPJ", "CNAE", "UF", "Município",
            "Capital social", "Porte (estimado)", "Situação", "Contato", "Racional da IA", "Fonte", "Gerado em" };
        for (var i = 0; i < cabec.Length; i++)
            ws.Cell(1, i + 1).Value = cabec[i];
        ws.Row(1).Style.Font.Bold = true;

        var linha = 2;
        foreach (var x in linhas)
        {
            var l = x.Lead;
            ws.Cell(linha, 1).Value = x.Score;
            ws.Cell(linha, 2).Value = l.RazaoSocial;
            ws.Cell(linha, 3).Value = CnpjUtil.Formatar(l.Cnpj);
            ws.Cell(linha, 4).Value = l.Cnae;
            ws.Cell(linha, 5).Value = l.Uf;
            ws.Cell(linha, 6).Value = l.Municipio;
            ws.Cell(linha, 7).Value = l.CapitalSocial;
            ws.Cell(linha, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(linha, 8).Value = l.PorteEstimado;
            ws.Cell(linha, 9).Value = l.Situacao;
            ws.Cell(linha, 10).Value = l.Contato ?? "não consta no cadastro";
            ws.Cell(linha, 11).Value = x.Racional;
            ws.Cell(linha, 12).Value = x.Fonte;
            ws.Cell(linha, 13).Value = x.GeradoEm.ToString("dd/MM/yyyy HH:mm");
            linha++;
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var nome = $"leads-prospeccao-{DateTime.Now:yyyyMMdd-HHmm}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nome);
    }
}
