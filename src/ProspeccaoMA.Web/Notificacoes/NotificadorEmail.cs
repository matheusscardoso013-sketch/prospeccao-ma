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
    private readonly IHttpClientFactory _http;

    public NotificadorEmail(AppDbContext db, IConfiguration cfg, ILogger<NotificadorEmail> log, IHttpClientFactory http)
    {
        _db = db;
        _cfg = cfg;
        _log = log;
        _http = http;
    }

    /// <summary>
    /// Envia um e-mail pelo provedor configurado. O free tier do Render BLOQUEIA as portas
    /// SMTP (25/465/587) desde set/2025, então o padrão é a API HTTP do Brevo (porta 443,
    /// não bloqueada; 300 e-mails/dia no plano gratuito). Email:Provedor="smtp" volta ao
    /// SMTP direto (funciona fora do Render ou em instância paga). Nunca lança.
    /// </summary>
    private async Task<bool> EnviarAsync(string assunto, string html, CancellationToken ct)
    {
        var para = _cfg["Email:Para"] ?? "";
        var de = _cfg["Email:De"] ?? _cfg["Email:Usuario"] ?? "prospeccao@valore.local";
        var destinos = para.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (destinos.Length == 0) return false;

        var provedor = (_cfg["Email:Provedor"] ?? "brevo").Trim().ToLowerInvariant();

        if (provedor == "smtp")
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(de, "Valore Brasil — Originação M&A"),
                Subject = assunto,
                Body = html,
                IsBodyHtml = true
            };
            foreach (var d in destinos) msg.To.Add(d);
            using var smtp = new SmtpClient(_cfg["Email:SmtpHost"], _cfg.GetValue("Email:SmtpPorta", 587))
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(_cfg["Email:Usuario"], _cfg["Email:Senha"])
            };
            await smtp.SendMailAsync(msg, ct);
            return true;
        }

        // Brevo (HTTP): https://developers.brevo.com — chave em Email:ApiKey.
        var apiKey = _cfg["Email:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _log.LogWarning("Email:ApiKey não configurada — e-mail não enviado (provedor {Prov}).", provedor);
            return false;
        }

        var corpo = new
        {
            sender = new { name = "Valore Brasil — Originação M&A", email = de },
            to = destinos.Select(d => new { email = d }).ToArray(),
            subject = assunto,
            htmlContent = html
        };

        var cliente = _http.CreateClient("email");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email");
        req.Headers.Add("api-key", apiKey);
        req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(corpo),
            Encoding.UTF8, "application/json");

        var resp = await cliente.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode) return true;

        var erro = await resp.Content.ReadAsStringAsync(ct);
        _log.LogError("Brevo recusou o envio ({Status}): {Erro}", (int)resp.StatusCode, Recortar(erro, 300));
        return false;
    }

    public async Task EnviarResumoDiarioAsync(CancellationToken ct = default)
    {
        try
        {
            var ativo = _cfg.GetValue("Email:Ativo", false);
            var para = _cfg["Email:Para"];
            if (!ativo || string.IsNullOrWhiteSpace(para))
            {
                _log.LogInformation("E-mail diário desativado/não configurado (Email:Ativo/Para) — pulando.");
                return;
            }

            // Dia de BRASÍLIA, não dia UTC: a rodada é ao meio-dia daqui, e o carimbo no
            // banco é UTC — comparar com UtcNow.Date jogaria o fim do dia para o dia seguinte.
            var hoje = Fuso.InicioHojeUtc();

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
                .Take(20)
                .ToListAsync(ct);

            if (leadsHoje.Count == 0 && paresHoje.Count == 0)
            {
                _log.LogInformation("Sem novidades hoje — e-mail diário não enviado.");
                return;
            }

            var html = MontarHtml(leadsHoje, paresHoje);
            // O que exige ação vai no assunto: prioritárias primeiro, o resto como contexto.
            var quentes = paresHoje.Count(p => p.Score >= 80);
            var assunto = quentes > 0
                ? $"Valore Brasil — {Fuso.Agora:dd/MM}: {quentes} oportunidade(s) prioritária(s), {leadsHoje.Count} lead(s) novo(s)"
                : $"Valore Brasil — {Fuso.Agora:dd/MM}: {leadsHoje.Count} lead(s) novo(s), {paresHoje.Count} aderência(s)";

            if (await EnviarAsync(assunto, html, ct))
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
            var para = _cfg["Email:Para"];
            if (!ativo || string.IsNullOrWhiteSpace(para)) return;

            var html = new StringBuilder();
            html.Append("<div style='font-family:Segoe UI,Arial,sans-serif;color:#1c2533;max-width:640px'>");
            html.Append("<h2 style='color:#0E3A56'>Oportunidade prioritária</h2>");
            html.Append($"<p><strong>{lead.RazaoSocial}</strong> × <strong>{comprador.Nome}</strong> — sinergia <strong>{score}/100</strong>" +
                        $"{(string.IsNullOrWhiteSpace(comprador.Responsavel) ? "" : $"<br/>Responsável: {comprador.Responsavel}")}</p>");
            html.Append($"<p style='color:#6b7686;font-size:13px'>{Recortar(racional, 300)}</p>");
            html.Append($"<p><a href='https://prospeccao-ma.onrender.com/Lead/Compradores/{lead.Id}' style='background:#0E3A56;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none'>Abrir a ficha do alvo</a></p>");
            html.Append("</div>");

            if (await EnviarAsync($"Sinergia {score}/100: {lead.RazaoSocial} × {comprador.Nome}", html.ToString(), ct))
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
            sb.Append("<h3>Leads do dia</h3><ul>");
            foreach (var s in leads)
                sb.Append($"<li><strong>{s.Lead?.RazaoSocial}</strong> — score {s.Score}/100<br/><span style='color:#6b7686;font-size:13px'>{Recortar(s.Racional, 180)}</span></li>");
            sb.Append("</ul>");
        }

        // Prioritárias separadas do resto: este é o ÚNICO e-mail do dia (o alerta por match
        // foi desligado — com 12 leads/dia virava uma enxurrada), então o que exige ação
        // precisa estar no topo, não diluído numa lista única.
        void Secao(string titulo, string cor, List<SinergiaComprador> itens)
        {
            if (itens.Count == 0) return;
            sb.Append($"<h3 style='color:{cor}'>{titulo}</h3><ul>");
            foreach (var p in itens)
                sb.Append($"<li style='margin-bottom:9px'><strong>{p.Lead?.RazaoSocial}</strong> × <strong>{p.Comprador?.Nome}</strong> — sinergia {p.Score}/100" +
                          $"{(string.IsNullOrWhiteSpace(p.Comprador?.Responsavel) ? "" : $" · resp.: {p.Comprador!.Responsavel}")}" +
                          $"<br/><span style='color:#6b7686;font-size:13px'>{Recortar(p.Racional, 160)}</span></li>");
            sb.Append("</ul>");
        }

        var prioritarias = pares.Where(p => p.Score >= 80).ToList();
        var demais = pares.Where(p => p.Score < 80).ToList();
        Secao($"Oportunidades prioritárias ({prioritarias.Count})", "#0e7c68", prioritarias);
        Secao("Demais aderências do dia", "#0E3A56", demais);

        sb.Append("<p><a href='https://prospeccao-ma.onrender.com/Mesa' style='background:#29A9E0;color:#fff;padding:10px 18px;border-radius:8px;text-decoration:none'>Abrir a Mesa de operações</a></p>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Recortar(string? s, int n)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length > n ? s[..n] + "…" : s);
}
