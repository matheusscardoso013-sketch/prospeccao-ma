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
    private readonly bool _usarTriagemIA;

    public MotorSinergia(AppDbContext db, IClassificadorIA ia, ILogger<MotorSinergia> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _log = log;
        _max = Math.Max(1, cfg.GetValue("Sinergia:MaxCompradoresPorLead", 12));
        _usarTriagemIA = cfg.GetValue("Sinergia:UsarTriagemIA", true);
    }

    public async Task<int> CruzarLeadAsync(int leadId, CancellationToken ct = default)
    {
        var lead = await _db.Leads.FirstOrDefaultAsync(l => l.Id == leadId, ct);
        return lead is null ? 0 : await CruzarLeadAsync(lead, ct);
    }

    public async Task<int> CruzarLeadAsync(Lead lead, CancellationToken ct = default)
    {
        // Sem tese não há contra o que avaliar — fica fora do confronto (e aparece no
        // filtro "⚠ Sem tese" da aba Compradores para o time correr atrás da informação).
        var compradores = await _db.Compradores
            .Where(c => c.Ativo && c.Tese.Length >= 20)
            .ToListAsync(ct);

        // Triagem semântica (1 chamada de IA escolhe os candidatos lendo as teses);
        // em falha/desligada, cai para o pré-filtro por palavras-chave do setor.
        List<Comprador> shortlist = new();
        if (_usarTriagemIA)
        {
            var ids = await _ia.SelecionarCompradoresAsync(lead, compradores, _max, ct);
            if (ids is { Count: > 0 })
            {
                shortlist = compradores.Where(c => ids.Contains(c.Id)).ToList();
                _log.LogInformation("Lead {Nome}: triagem IA selecionou {N} comprador(es)", lead.RazaoSocial, shortlist.Count);
            }
        }

        if (shortlist.Count == 0)
            shortlist = ShortlistPorKeywords(lead, compradores);

        if (shortlist.Count == 0)
        {
            _log.LogInformation("Lead {Nome}: nenhum comprador aderente na triagem/pré-filtro", lead.RazaoSocial);
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

            var sinergia = new SinergiaComprador { LeadId = lead.Id, CompradorId = comprador.Id };

            // Filtro duro: incompatibilidade aritmética com os critérios estruturados
            // elimina sem gastar chamada de IA (o par fica registrado como descartado).
            var eliminacao = FiltroDuro(lead, comprador);
            if (eliminacao is not null)
            {
                AplicarResultado(sinergia, new ResultadoClassificacao(10, eliminacao, Porte: 0));
            }
            else
            {
                var r = await _ia.ClassificarSinergiaAsync(lead, comprador, ct);
                AplicarResultado(sinergia, r);
            }

            _db.SinergiasComprador.Add(sinergia);
            novos++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Lead {Cnpj}: {N} sinergia(s) de comprador gerada(s)", lead.Cnpj, novos);
        return novos;
    }

    private const string MarcaDescarteAuto = "descartado automaticamente (score baixo)";

    /// <summary>Grava o resultado da IA na sinergia e aplica o descarte automático:
    /// pares fracos (1-39) não poluem a Mesa; se um recálculo melhorar o score de um par
    /// auto-descartado, ele volta para Novo. Score 0 = falha da IA (auto-cura reprocessa).</summary>
    internal static void AplicarResultado(SinergiaComprador s, ResultadoClassificacao r)
    {
        s.Score = r.Score;
        s.ScoreSetor = r.Setor;
        s.ScorePorte = r.Porte;
        s.ScoreModelo = r.Modelo;
        s.ScoreGeo = r.Geo;
        s.Racional = r.Racional;
        s.GeradoEm = DateTime.UtcNow;

        if (r.Score is > 0 and < 40 && s.Status == StatusSinergia.Novo)
        {
            s.Status = StatusSinergia.Descartado;
            s.Anotacoes ??= MarcaDescarteAuto;
        }
        else if (r.Score >= 40 && s.Status == StatusSinergia.Descartado && s.Anotacoes == MarcaDescarteAuto)
        {
            s.Status = StatusSinergia.Novo;
            s.Anotacoes = null;
        }
    }

    /// <summary>Eliminação aritmética pelos critérios estruturados do comprador — só quando a
    /// incompatibilidade é gritante (margem de 3x), para nunca descartar um caso discutível.
    /// Devolve o motivo, ou null se o par merece ir à IA.</summary>
    private static string? FiltroDuro(Lead lead, Comprador comprador)
    {
        var fat = FaturamentoEstimado(lead);
        if (fat is null) return null; // sem número confiável, não elimina

        if (comprador.FaturamentoMinAlvo is decimal min && min > 0 && fat < min / 3)
            return $"Eliminado por critério estruturado (sem consulta à IA): faturamento estimado ({fat.Value:C0}) " +
                   $"muito abaixo do mínimo buscado pelo comprador ({min:C0}).";

        if (comprador.FaturamentoMaxAlvo is decimal max && max > 0 && fat > max * 3)
            return $"Eliminado por critério estruturado (sem consulta à IA): faturamento estimado ({fat.Value:C0}) " +
                   $"muito acima do teto buscado pelo comprador ({max:C0}).";

        return null;
    }

    /// <summary>Extrai um faturamento numérico do PorteEstimado quando ele traz valor em R$
    /// (ex.: "~ R$ 8.000.000"). Textos sem valor ("~ Grande porte") devolvem null.
    /// Ignora centavos (parte após a vírgula) para não inflar o número em 100x.</summary>
    private static decimal? FaturamentoEstimado(Lead lead)
    {
        var texto = lead.PorteEstimado ?? "";
        if (!texto.Contains("R$")) return null;
        var semCentavos = texto.Split(',')[0];
        var digitos = new string(semCentavos.Where(char.IsDigit).ToArray());
        if (digitos.Length < 5 || digitos.Length > 15) return null; // fora disso não é faturamento plausível
        return decimal.TryParse(digitos, out var v) ? v : null;
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
            var elim = FiltroDuro(s.Lead, comprador);
            var r = elim is not null
                ? new ResultadoClassificacao(10, elim, Porte: 0)
                : await _ia.ClassificarSinergiaAsync(s.Lead, comprador, ct);
            AplicarResultado(s, r);
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
            var elim = FiltroDuro(lead, s.Comprador);
            var r = elim is not null
                ? new ResultadoClassificacao(10, elim, Porte: 0)
                : await _ia.ClassificarSinergiaAsync(lead, s.Comprador, ct);
            AplicarResultado(s, r);
            n++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Lead {Nome}: {N} sinergia(s) recalculada(s) com os dados atuais", lead.RazaoSocial, n);
        return n;
    }

    /// <summary>Fallback barato: ordena compradores por sobreposição de palavras-chave do setor.</summary>
    private List<Comprador> ShortlistPorKeywords(Lead lead, List<Comprador> compradores)
    {
        var kws = KeywordsDoCnae(lead.Cnae);
        // Alvos curados da Valore não têm CNAE — derivamos as keywords do segmento textual.
        if (kws.Length == 0)
            kws = KeywordsDeTexto(lead.Segmento, lead.Descricao);

        return compradores
            .Select(c => new { Comprador = c, Peso = Overlap(c, kws) })
            .Where(x => x.Peso > 0)
            .OrderByDescending(x => x.Peso)
            .Take(_max)
            .Select(x => x.Comprador)
            .ToList();
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
