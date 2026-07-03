using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Controllers;

/// <summary>Atualização de status/anotações de um match (pipeline de trabalho do time).</summary>
[Authorize]
public class SinergiaController : Controller
{
    private readonly AppDbContext _db;
    public SinergiaController(AppDbContext db) => _db = db;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Atualizar(int id, StatusSinergia status, string? anotacoes, string? voltar)
    {
        var s = await _db.SinergiasComprador.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();

        var statusAnterior = s.Status;
        s.Status = status;
        s.Anotacoes = string.IsNullOrWhiteSpace(anotacoes) ? null : anotacoes.Trim();
        s.AtualizadoEm = DateTime.UtcNow;

        // Feedback loop: ao descartar, a anotação vale como motivo (exemplo negativo p/ a IA).
        if (status == StatusSinergia.Descartado && !string.IsNullOrWhiteSpace(s.Anotacoes))
            s.MotivoDescarte = s.Anotacoes;

        if (statusAnterior != status)
            RegistrarInteracao(s, $"Status: {Util.StatusUi.Rotulo(statusAnterior)} → {Util.StatusUi.Rotulo(status)}");

        await _db.SaveChangesAsync();
        TempData["Ok"] = "Match atualizado.";
        return LocalRedirect(string.IsNullOrWhiteSpace(voltar) ? "/Mesa" : voltar);
    }

    /// <summary>Move um match para outro status (usado pelo arrastar-e-soltar do quadro Kanban).
    /// Só altera o status; responde JSON sem redirecionar. Ao descartar, o quadro pergunta o
    /// motivo — que vira exemplo negativo no matching daquele comprador (feedback loop).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Mover(int id, StatusSinergia status, string? motivo = null)
    {
        var s = await _db.SinergiasComprador.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();

        var statusAnterior = s.Status;
        s.Status = status;
        s.AtualizadoEm = DateTime.UtcNow;
        if (status == StatusSinergia.Descartado && !string.IsNullOrWhiteSpace(motivo))
            s.MotivoDescarte = motivo.Trim();

        if (statusAnterior != status)
            RegistrarInteracao(s, $"Status: {Util.StatusUi.Rotulo(statusAnterior)} → {Util.StatusUi.Rotulo(status)}" +
                (string.IsNullOrWhiteSpace(motivo) ? "" : $" ({motivo.Trim()})"));

        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    /// <summary>Anotação datada e assinada no histórico do match (mini-CRM).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Anotar(int id, string texto, string? voltar)
    {
        var s = await _db.SinergiasComprador.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(texto))
        {
            RegistrarInteracao(s, texto.Trim());
            s.AtualizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Ok"] = "Anotação registrada.";
        }
        return LocalRedirect(string.IsNullOrWhiteSpace(voltar) ? "/Mesa" : voltar);
    }

    /// <summary>Agenda a próxima ação do match (lembrete do mini-CRM).</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProximaAcao(int id, DateTime? quando, string? nota, string? voltar)
    {
        var s = await _db.SinergiasComprador.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound();

        s.ProximaAcaoEm = quando is null ? null : DateTime.SpecifyKind(quando.Value, DateTimeKind.Utc);
        s.ProximaAcaoNota = string.IsNullOrWhiteSpace(nota) ? null : nota.Trim();
        s.AtualizadoEm = DateTime.UtcNow;
        if (quando is not null)
            RegistrarInteracao(s, $"Próxima ação em {quando:dd/MM}: {s.ProximaAcaoNota ?? "—"}");

        await _db.SaveChangesAsync();
        TempData["Ok"] = quando is null ? "Lembrete removido." : "Próxima ação agendada.";
        return LocalRedirect(string.IsNullOrWhiteSpace(voltar) ? "/Mesa" : voltar);
    }

    private void RegistrarInteracao(SinergiaComprador s, string texto)
        => _db.InteracoesMatch.Add(new InteracaoMatch
        {
            SinergiaId = s.Id,
            Autor = User.Identity?.Name ?? "sistema",
            Texto = texto,
            Em = DateTime.UtcNow
        });
}
