using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Ingestao;
using ProspeccaoMA.Web.Jobs;
using ProspeccaoMA.Web.Models;
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

    /// <summary>Edição de qualquer lead. Os dados oficiais de identidade (CNPJ, CNAE, UF,
    /// capital — vindos da Receita) não são editáveis para preservar a rastreabilidade;
    /// o restante (contatos, segmento, resumo, estimativas) pode ser ajustado à mão, e o
    /// lead fica marcado como "editado manualmente".</summary>
    [HttpGet]
    public async Task<IActionResult> Editar(int id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        return View(lead);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SalvarLead(Models.Lead modelo)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == modelo.Id);
        if (lead is null) return NotFound();

        if (string.IsNullOrWhiteSpace(modelo.RazaoSocial))
        {
            ModelState.AddModelError(string.Empty, "A razão social é obrigatória.");
            return View("Editar", modelo);
        }

        lead.RazaoSocial = modelo.RazaoSocial.Trim();
        lead.Segmento = modelo.Segmento;
        lead.Contato = modelo.Contato;
        lead.Site = modelo.Site;
        lead.Responsavel = modelo.Responsavel;
        lead.PorteEstimado = modelo.PorteEstimado ?? string.Empty;
        lead.MargemEbitda = modelo.MargemEbitda;
        lead.ValuationEstimado = modelo.ValuationEstimado;
        lead.ModeloNegocio = modelo.ModeloNegocio;
        lead.Abrangencia = modelo.Abrangencia;
        lead.Cultura = modelo.Cultura;
        lead.Descricao = modelo.Descricao;
        lead.EditadoManualmente = true;

        await _db.SaveChangesAsync();
        TempData["Ok"] = "Alvo atualizado. Se mudou o resumo/segmento, use ↻ Recalcular na tela de compradores compatíveis.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Exclui a empresa e seus cruzamentos. Obs.: um lead da Receita excluído pode
    /// reaparecer num futuro reimport em massa do recorte (a exclusão não é uma lista de bloqueio).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Excluir(int id)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == id);
        if (lead is null) return NotFound();
        _db.Leads.Remove(lead); // LeadScores e SinergiasComprador caem em cascata
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Empresa \"{lead.RazaoSocial}\" excluída (com seus cruzamentos).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Recalcular(int id)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var n = await _motor.RecalcularLeadAsync(id, cts.Token);
        TempData["Ok"] = $"{n} sinergia(s) recalculada(s) com os dados atuais do alvo.";
        return RedirectToAction(nameof(Compradores), new { id });
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
            // Média só de quem já foi pontuado (score > 0) — alvos curados ainda não
            // cruzados não diluem o indicador.
            ScoreMedio = linhas.Any(l => l.Score > 0)
                ? (int)Math.Round(linhas.Where(l => l.Score > 0).Average(l => l.Score))
                : 0,
            UltimaExecucao = await _db.ExecucoesJob.OrderByDescending(e => e.IniciadoEm).FirstOrDefaultAsync(),
            Ufs = await _db.Leads.Where(l => l.Uf != "").Select(l => l.Uf).Distinct().OrderBy(u => u).ToListAsync(),
            Cnaes = await _db.Leads.Where(l => l.Cnae != "").Select(l => l.Cnae).Distinct().OrderBy(c => c).ToListAsync()
        };
        return View(vm);
    }

    /// <summary>Monta as linhas (lead + melhor score) aplicando os filtros. Reusado pelo export.
    /// Inclui os alvos curados da Valore (sem LeadScore): para eles, o score exibido é o da
    /// melhor sinergia com um comprador.</summary>
    private async Task<List<LeadLinha>> CarregarLinhasAsync(string? cnae, string? uf, int? scoreMin, OrdenacaoLeads ordenar)
    {
        var query = _db.Leads.Include(l => l.Scores)
            .Where(l => l.Scores.Any() || l.Origem != Models.Lead.OrigemReceita);

        if (!string.IsNullOrWhiteSpace(uf))
            query = query.Where(l => l.Uf == uf);
        if (!string.IsNullOrWhiteSpace(cnae))
            query = query.Where(l => l.Cnae == cnae);

        var leads = await query.ToListAsync();

        // Melhor sinergia de comprador para TODOS os leads exibidos (vira a tag "🎯 melhor
        // comprador" no card; para curados sem LeadScore, é também o score principal).
        var ids = leads.Select(l => l.Id).ToList();
        var melhoresSinergias = ids.Count == 0
            ? new Dictionary<int, SinergiaComprador>()
            : (await _db.SinergiasComprador.Include(s => s.Comprador)
                  .Where(s => ids.Contains(s.LeadId)).ToListAsync())
              .GroupBy(s => s.LeadId)
              .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.Score).First());

        var linhas = leads.Select(l =>
        {
            melhoresSinergias.TryGetValue(l.Id, out var sin);

            if (l.Scores.Count > 0)
            {
                // Leads da Receita: a nota principal é a ADERÊNCIA ao mandato configurado.
                var melhor = l.Scores.OrderByDescending(s => s.Score).First();
                return new LeadLinha(l, melhor.Score, melhor.Racional, melhor.Fonte, melhor.GeradoEm,
                    "aderência", sin?.Comprador?.Nome, sin?.Score);
            }

            if (sin is not null)
                return new LeadLinha(l, sin.Score, sin.Racional, l.Origem, sin.GeradoEm,
                    "sinergia", sin.Comprador?.Nome, sin.Score);

            return new LeadLinha(l, 0,
                "Ainda não cruzado com compradores — abra 🎯 Compradores ou aguarde a esteira diária.",
                l.Origem, l.CriadoEm, "sinergia");
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

        string[] cabec = { "Score", "Tipo do score", "Razão social", "CNPJ", "CNAE", "UF", "Município",
            "Capital social", "Porte (estimado)", "Situação", "Contato", "Racional da IA",
            "Melhor comprador", "Sinergia do melhor comprador", "Fonte", "Gerado em" };
        for (var i = 0; i < cabec.Length; i++)
            ws.Cell(1, i + 1).Value = cabec[i];
        ws.Row(1).Style.Font.Bold = true;

        var linha = 2;
        foreach (var x in linhas)
        {
            var l = x.Lead;
            ws.Cell(linha, 1).Value = x.Score;
            ws.Cell(linha, 2).Value = x.RotuloScore;
            ws.Cell(linha, 3).Value = l.RazaoSocial;
            ws.Cell(linha, 4).Value = CnpjUtil.Formatar(l.Cnpj);
            ws.Cell(linha, 5).Value = l.Cnae;
            ws.Cell(linha, 6).Value = l.Uf;
            ws.Cell(linha, 7).Value = l.Municipio;
            ws.Cell(linha, 8).Value = l.CapitalSocial;
            ws.Cell(linha, 8).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(linha, 9).Value = l.PorteEstimado;
            ws.Cell(linha, 10).Value = l.Situacao;
            ws.Cell(linha, 11).Value = l.Contato ?? "não consta no cadastro";
            ws.Cell(linha, 12).Value = x.Racional;
            ws.Cell(linha, 13).Value = x.MelhorComprador ?? "";
            ws.Cell(linha, 14).Value = x.MelhorSinergiaScore?.ToString() ?? "";
            ws.Cell(linha, 15).Value = x.Fonte;
            ws.Cell(linha, 16).Value = x.GeradoEm.ToString("dd/MM/yyyy HH:mm");
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
