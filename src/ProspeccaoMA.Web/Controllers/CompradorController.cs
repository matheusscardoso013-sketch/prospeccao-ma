using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Util;

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

    public async Task<IActionResult> Index(string? busca, string? resp, string? tipo, bool semTese = false)
    {
        var query = _db.Compradores.Include(c => c.Sinergias).Where(c => c.Ativo);

        if (semTese)
            query = query.ForaDoCruzamento();
        if (!string.IsNullOrWhiteSpace(resp))
            query = query.Where(c => c.Responsavel == resp);
        if (!string.IsNullOrWhiteSpace(tipo))
            query = query.Where(c => c.TipoEmpresa == tipo);

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
        ViewData["Resp"] = resp;
        ViewData["Tipo"] = tipo;
        ViewData["SemTese"] = semTese;
        ViewData["Total"] = await _db.Compradores.CountAsync(c => c.Ativo);
        ViewData["TotalSemTese"] = await _db.Compradores.Where(c => c.Ativo).ForaDoCruzamento().CountAsync();
        ViewData["AValidar"] = await _db.Compradores.CountAsync(c => c.Ativo && c.CriteriosExtraidosEm != null && !c.CriteriosValidados);
        ViewData["Responsaveis"] = await _db.Compradores
            .Where(c => c.Ativo && c.Responsavel != null && c.Responsavel != "")
            .Select(c => c.Responsavel!).Distinct().OrderBy(r => r).ToListAsync();
        ViewData["Tipos"] = await _db.Compradores
            .Where(c => c.Ativo && c.TipoEmpresa != null && c.TipoEmpresa != "")
            .Select(c => c.TipoEmpresa!).Distinct().OrderBy(t => t).ToListAsync();

        // Ordena por completude do cadastro (prontos p/ matching primeiro) e, dentro do
        // grupo, por atividade (quem tem mais matches sobe) — vira um painel de qualidade.
        var lista = (await query.ToListAsync())
            .OrderBy(c => GrupoCompletude(c))
            .ThenByDescending(c => c.Sinergias.Count)
            .ThenBy(c => c.Nome)
            .ToList();
        return View(lista);
    }

    /// <summary>Fila de revisão: critérios extraídos automaticamente da tese pela IA,
    /// aguardando o olho do time. Confirmar não altera nada — só marca como validado;
    /// se algo estiver errado, o caminho é Editar (que também valida).</summary>
    public async Task<IActionResult> Revisao()
    {
        var pendentes = await _db.Compradores
            .Where(c => c.Ativo && c.CriteriosExtraidosEm != null && !c.CriteriosValidados)
            .OrderBy(c => c.Nome)
            .ToListAsync();
        return View(pendentes);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ValidarCriterios(int id)
    {
        var c = await _db.Compradores.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        c.CriteriosValidados = true;
        await _db.SaveChangesAsync();
        TempData["Ok"] = $"Critérios de {c.Nome} validados.";
        return RedirectToAction(nameof(Revisao));
    }

    /// <summary>Ordem de completude do cadastro: 0 = tese + critérios estruturados ("pronto");
    /// 1 = só tese em texto; 2 = sem tese, mas cruzável pelo perfil do site; 3 = fora do
    /// cruzamento (sem tese e sem perfil).</summary>
    public static int GrupoCompletude(Comprador c)
    {
        if (!Util.Teses.EhCruzavel(c)) return 3;
        if (!Util.Teses.TemTeseUtil(c)) return 2; // entra pelo perfil do site
        var temCriterios = c.FaturamentoMinAlvo is not null || c.FaturamentoMaxAlvo is not null
            || c.MargemEbitdaMinima is not null || !string.IsNullOrWhiteSpace(c.ModeloNegocioAlvo)
            || !string.IsNullOrWhiteSpace(c.Exclusoes) || !string.IsNullOrWhiteSpace(c.GeografiaAlvo);
        return temCriterios ? 0 : 1;
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

        // Passou pelo olho do time ao salvar a edição — critérios considerados validados.
        if (c.CriteriosExtraidosEm is not null) c.CriteriosValidados = true;

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
            .Include(s => s.Interacoes)
            .Where(s => s.CompradorId == id)
            .OrderByDescending(s => s.Score)
            .ToListAsync();

        ViewData["Comprador"] = comprador;
        return View(sinergias);
    }
}
