using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Matching;

public interface IMotorSinergia
{
    /// <summary>Cruza um lead com os compradores compatíveis (pré-filtro + IA). Idempotente.</summary>
    Task<int> CruzarLeadAsync(int leadId, CancellationToken ct = default);
    Task<int> CruzarLeadAsync(Lead lead, CancellationToken ct = default);

    /// <summary>Reavalia (re-pontua) as sinergias existentes de um comprador com a tese ATUAL.</summary>
    Task<int> RecalcularCompradorAsync(int compradorId, CancellationToken ct = default);

    /// <summary>Reavalia as sinergias existentes de um lead com os dados ATUAIS dele (após edição).</summary>
    Task<int> RecalcularLeadAsync(int leadId, CancellationToken ct = default);
}

/// <summary>
/// Cruza leads REAIS com a base de compradores: pré-filtra por aderência de setor
/// (palavras-chave derivadas do CNAE × tipo/segmento/tags/tese do comprador) para limitar
/// o custo, e a IA pontua a sinergia (0-100) só do shortlist contra a tese. Idempotente
/// por (lead, comprador). A IA não inventa — só avalia o fit dos dados reais.
/// </summary>
public class MotorSinergia : IMotorSinergia
{
    private readonly AppDbContext _db;
    private readonly IClassificadorIA _ia;
    private readonly ILogger<MotorSinergia> _log;
    private readonly int _max;

    public MotorSinergia(AppDbContext db, IClassificadorIA ia, ILogger<MotorSinergia> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _log = log;
        _max = Math.Max(1, cfg.GetValue("Sinergia:MaxCompradoresPorLead", 12));
    }

    public async Task<int> CruzarLeadAsync(int leadId, CancellationToken ct = default)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        return lead is null ? 0 : await CruzarLeadAsync(lead, ct);
    }

    public async Task<int> CruzarLeadAsync(Lead lead, CancellationToken ct = default)
    {
        var kws = KeywordsDoCnae(lead.Cnae);
        // Alvos curados da Valore não têm CNAE — derivamos as keywords do segmento textual.
        if (kws.Length == 0)
            kws = KeywordsDeTexto(lead.Segmento, lead.Descricao);

        var compradores = await _db.Compradores.Where(c => c.Ativo).ToListAsync(ct);

        // Pré-filtro barato: ordena por aderência de setor; só os de overlap > 0, até _max.
        var shortlist = compradores
            .Select(c => new { Comprador = c, Peso = Overlap(c, kws) })
            .Where(x => x.Peso > 0)
            .OrderByDescending(x => x.Peso)
            .Take(_max)
            .Select(x => x.Comprador)
            .ToList();

        if (shortlist.Count == 0)
        {
            _log.LogInformation("Lead {Cnpj}: nenhum comprador aderente no pré-filtro", lead.Cnpj);
            return 0;
        }

        // Já cruzados (idempotência).
        var jaFeitos = await _db.SinergiasComprador
            .Where(s => s.LeadId == lead.Id)
            .Select(s => s.CompradorId)
            .ToListAsync(ct);

        var novos = 0;
        foreach (var comprador in shortlist)
        {
            if (jaFeitos.Contains(comprador.Id)) continue;
            ct.ThrowIfCancellationRequested();

            var r = await _ia.ClassificarSinergiaAsync(lead, comprador, ct);
            _db.SinergiasComprador.Add(new SinergiaComprador
            {
                LeadId = lead.Id,
                CompradorId = comprador.Id,
                Score = r.Score,
                Racional = r.Racional,
                GeradoEm = DateTime.UtcNow
            });
            novos++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Lead {Cnpj}: {N} sinergia(s) de comprador gerada(s)", lead.Cnpj, novos);
        return novos;
    }

    public async Task<int> RecalcularCompradorAsync(int compradorId, CancellationToken ct = default)
    {
        var comprador = await _db.Compradores.FirstOrDefaultAsync(c => c.Id == compradorId, ct);
        if (comprador is null) return 0;

        var sinergias = await _db.SinergiasComprador
            .Include(s => s.Lead)
            .Where(s => s.CompradorId == compradorId)
            .ToListAsync(ct);

        var n = 0;
        foreach (var s in sinergias)
        {
            if (s.Lead is null) continue;
            ct.ThrowIfCancellationRequested();
            var r = await _ia.ClassificarSinergiaAsync(s.Lead, comprador, ct);
            s.Score = r.Score;
            s.Racional = r.Racional;
            s.GeradoEm = DateTime.UtcNow;
            n++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Comprador {Nome}: {N} sinergia(s) recalculada(s) com a tese atual", comprador.Nome, n);
        return n;
    }

    public async Task<int> RecalcularLeadAsync(int leadId, CancellationToken ct = default)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        if (lead is null) return 0;

        var sinergias = await _db.SinergiasComprador
            .Include(s => s.Comprador)
            .Where(s => s.LeadId == leadId)
            .ToListAsync(ct);

        var n = 0;
        foreach (var s in sinergias)
        {
            if (s.Comprador is null) continue;
            ct.ThrowIfCancellationRequested();
            var r = await _ia.ClassificarSinergiaAsync(lead, s.Comprador, ct);
            s.Score = r.Score;
            s.Racional = r.Racional;
            s.GeradoEm = DateTime.UtcNow;
            n++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Lead {Nome}: {N} sinergia(s) recalculada(s) com os dados atuais", lead.RazaoSocial, n);
        return n;
    }

    private static int Overlap(Comprador c, string[] kws)
    {
        var texto = string.Join(" ", new[] { c.TipoEmpresa, c.Segmento, c.SegmentoClientes, c.Tags, c.Tese })
            .ToLowerInvariant();
        var n = kws.Count(k => texto.Contains(k));
        if (texto.Contains("diversificad")) n += 1; // fundos generalistas entram na disputa
        return n;
    }

    /// <summary>Keywords a partir do segmento textual (alvos curados): palavras significativas (≥4 letras).</summary>
    private static string[] KeywordsDeTexto(string? segmento, string? descricao)
    {
        var fonte = $"{segmento} {(descricao ?? "").Split('\n').FirstOrDefault()}".ToLowerInvariant();
        return fonte
            .Split(new[] { ' ', ',', ';', '/', '(', ')', '.', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length >= 4 && p != "para" && p != "como" && p != "empresa" && p != "tipo")
            .Distinct()
            .Take(15)
            .ToArray();
    }

    private static string[] KeywordsDoCnae(string cnae)
    {
        var d = new string((cnae ?? string.Empty).Where(char.IsDigit).ToArray());
        bool P(string p) => d.StartsWith(p);
        if (P("62") || P("63"))
            return new[] { "tecnologia", "software", "ti ", "saas", "tech", "sistema", "digital", "dados", "cloud", "aplicativo", "plataforma" };
        if (P("86") || P("21") || P("3250"))
            return new[] { "saude", "saúde", "clinic", "clínic", "hospital", "medic", "médic", "farma", "diagnóstic", "diagnostic", "odonto", "health" };
        if (P("01") || P("02") || P("03"))
            return new[] { "agro", "agronegócio", "agronegocio", "agricultura", "pecuária", "pecuaria", "rural", "fazenda", "grãos", "graos" };
        if (P("10"))
            return new[] { "aliment", "food", "laticínio", "laticinio", "bebida", "frigorífic", "frigorific" };
        if (P("463"))
            return new[] { "atacado", "distribuição", "distribuicao", "aliment", "agro", "varejo" };
        return Array.Empty<string>();
    }
}
