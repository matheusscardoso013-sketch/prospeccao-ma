using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Util;

namespace ProspeccaoMA.Web.Notificacoes;

public interface INotificadorEmail
{
    /// <summary>Envia o resumo diário (leads do dia + melhores compradores). Nunca lança.</summary>
    Task EnviarResumoDiarioAsync(CancellationToken ct = default);

    /// <summary>Alerta imediato de match quente (score >= 80): o time fica sabendo na hora,
    /// sem esperar o resumo diário. Nunca lança.</summary>
    Task EnviarMatchQuenteAsync(Lead lead, Comprador comprador, int score, string racional, CancellationToken ct = default);
}

/// <summary>
/// E-mail diário pós-rotina das 12h: leads novos do dia com seus melhores compradores,
/// para o time agir sem precisar abrir o painel. Configurado via seção Email (env vars no
/// Render): Email__Ativo, Email__SmtpHost, Email__SmtpPorta, Email__Usuario, Email__Senha,
/// Email__De, Email__Para (vários separados por vírgula). Sem configuração, apenas loga e segue.
/// </summary>
public class NotificadorEmail : INotificadorEmail
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<NotificadorEmail> _log;

    public NotificadorEmail(AppDbContext db, IConfiguration cfg, ILogger<NotificadorEmail> log)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
    }

    public async Task EnviarResumoDiarioAsync(CancellationToken ct = default)
    {
        try
        {
            var ativo = _cfg.GetValue("Email:Ativo", false);
            var host = _cfg["Email:SmtpHost"];
            var para = _cfg["Email:Para"];
            if (!ativo || string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(para))
            {
                _log.LogInformation("E-mail diário desativado/não configurado (Email:Ativo/SmtpHost/Para) — pulando.");
                return;
            }

            var hoje = DateTime.UtcNow.Date;

            var leadsHoje = await _db.LeadScores
                .Include(s => s.Lead)
                .Where(s => s.GeradoEm >= hoje)
                .OrderByDescending(s => s.Score)
                .Take(10)
                .ToListAsync(ct);

            var paresHoje = await _db.SinergiasComprador
                .Include(s => s.Lead).Include(s => s.Comprador)
                .Where(s => s.GeradoEm >= hoje && s.Score >= 50)
                .OrderByDescending(s => s.Score)
                .Take(15)
                .ToListAsync(ct);

            if (leadsHoje.Count == 0 && paresHoje.Count == 0)
            {
                _log.LogInformation("Sem novidades hoje — e-mail diário não enviado.");
                return;
            }

            var html = MontarHtml(leadsHoje, paresHoje);

            var de = _cfg["Email:De"] ?? _cfg["Email:Usuario"] ?? "prospeccao@valore.local";
            using var msg = new MailMessage
            {
                From = new MailAddress(de, "Valore Brasil — Originação M&A"),
                Subject = $"Valore Brasil — {Fuso.Brasil(DateTime.UtcNow):dd/MM}: {leadsHoje.Count} lead(s) novo(s), {paresHoje.Count} match(es)",
                Body = html,
                IsBodyHtml = true
            };
            foreach (var dest in para.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                msg.To.Add(dest);

            using var smtp = new SmtpClient(host, _cfg.GetValue("Email:SmtpPorta", 587))
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(_cfg["Email:Usuario"], _cfg["Email:Senha"])
            };
            await smtp.SendMailAsync(msg, ct);
            _log.LogInformation("E-mail diário enviado para {Para}.", para);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao enviar o e-mail diário (rotina segue normalmente).");
        }
    }

    public async Task EnviarMatchQuenteAsync(Lead lead, Comprador comprador, int score, string racional, CancellationToken ct = default)
    {
        try
        {
            var ativo = _cfg.GetValue("Email:Ativo", false);
            var host = _cfg["Email:SmtpHost"];
            var para = _cfg["Email:Para"];
            if (!ativo || string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(para)) return;

            var html = new StringBuilder();
            html.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#1c2533;max-width:640px'>");
            html.Append("<h2 style='color:#0E3A56'>🔥 Match quente na mesa</h2>");
            html.Append($"<p><strong>{lead.RazaoSocial}</strong> × <strong>{comprador.Nome}</strong> — sinergia <strong>{score}/100</strong>" +
                        $"{(string.IsNullOrWhiteSpace(comprador.Responsavel) ? "" : $"<br/>Responsável: {comprador.Responsavel}")}</p>");
            html.Append($"<p style='color:#6b7686;font-size:13px'>{Recortar(racional, 300)}</p>");
            html.Append($"<p><a href='https://prospeccao-ma.onrender.com/Lead/Compradores/{lead.Id}' style='background:#0E3A56;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none'>Abrir a ficha do alvo</a></p>");
            html.Append("</div>");

            var de = _cfg["Email:De"] ?? _cfg["Email:Usuario"] ?? "prospeccao@valore.local";
            using var msg = new MailMessage
            {
                From = new MailAddress(de, "Valore Brasil — Originação M&A"),
                Subject = $"🔥 Match {score}/100: {lead.RazaoSocial} × {comprador.Nome}",
                Body = html.ToString(),
                IsBodyHtml = true
            };
            foreach (var dest in para.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                msg.To.Add(dest);

            using var smtp = new SmtpClient(host, _cfg.GetValue("Email:SmtpPorta", 587))
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(_cfg["Email:Usuario"], _cfg["Email:Senha"])
            };
            await smtp.SendMailAsync(msg, ct);
            _log.LogInformation("Alerta de match quente enviado ({Lead} × {Comprador}, {Score}).", lead.RazaoSocial, comprador.Nome, score);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Falha ao enviar alerta de match quente (fluxo segue normalmente).");
        }
    }

    private static string MontarHtml(List<LeadScore> leads, List<SinergiaComprador> pares)
    {
        var sb = new StringBuilder();
        sb.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#1c2533;max-width:640px'>");
        sb.Append("<h2 style='color:#0E3A56'><span style='color:#29A9E0'>valore</span> BRASIL — resumo do dia</h2>");

        if (leads.Count > 0)
        {
            sb.Append("<h3>🆕 Leads do dia</h3><ul>");
            foreach (var s in leads)
                sb.Append($"<li><strong>{s.Lead?.RazaoSocial}</strong> — score {s.Score}/100<br/><span style='color:#6b7686;font-size:13px'>{Recortar(s.Racional, 180)}</span></li>");
            sb.Append("</ul>");
        }

        if (pares.Count > 0)
        {
            sb.Append("<h3>🎯 Melhores matches do dia (alvo × comprador)</h3><ul>");
            foreach (var p in pares)
                sb.Append($"<li><strong>{p.Lead?.RazaoSocial}</strong> × <strong>{p.Comprador?.Nome}</strong> — sinergia {p.Score}/100" +
                          $"{(string.IsNullOrWhiteSpace(p.Comprador?.Responsavel) ? "" : $" · resp.: {p.Comprador!.Responsavel}")}" +
                          $"<br/><span style='color:#6b7686;font-size:13px'>{Recortar(p.Racional, 160)}</span></li>");
            sb.Append("</ul>");
        }

        sb.Append("<p><a href='https://prospeccao-ma.onrender.com/Mesa' style='background:#29A9E0;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none'>Abrir a Mesa de operações</a></p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Recortar(string? s, int n)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length > n ? s[..n] + "…" : s);
}
