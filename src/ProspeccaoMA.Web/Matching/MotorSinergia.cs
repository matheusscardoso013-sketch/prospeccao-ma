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
    private readonly Notificacoes.INotificadorEmail _email;
    private readonly ILogger<MotorSinergia> _log;
    private readonly int _max;
    private readonly bool _usarTriagemIA;
    private readonly bool _usarEmbeddings;
    private readonly bool _doisEstagios;
    private readonly int _limiarSegundoEstagio;

    public MotorSinergia(AppDbContext db, IClassificadorIA ia, Notificacoes.INotificadorEmail email,
        ILogger<MotorSinergia> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _email = email;
        _log = log;
        _max = Math.Max(1, cfg.GetValue("Sinergia:MaxCompradoresPorLead", 12));
        _usarTriagemIA = cfg.GetValue("Sinergia:UsarTriagemIA", true);
        _usarEmbeddings = cfg.GetValue("Sinergia:UsarEmbeddings", true);
        _doisEstagios = cfg.GetValue("Sinergia:DoisEstagios", false);
        _limiarSegundoEstagio = cfg.GetValue("Sinergia:LimiarSegundoEstagio", 70);
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

        // Cascata de triagem: 1) similaridade vetorial (embeddings — determinística, cota
        // separada); 2) triagem semântica por LLM; 3) palavras-chave do setor.
        List<Comprador> shortlist = new();
        if (_usarEmbeddings)
        {
            shortlist = await ShortlistPorEmbeddingsAsync(lead, compradores, ct);
            if (shortlist.Count > 0)
                _log.LogInformation("Lead {Nome}: triagem por embeddings selecionou {N} comprador(es)", lead.RazaoSocial, shortlist.Count);
        }

        if (shortlist.Count == 0 && _usarTriagemIA)
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
            // Cota morta: parar de criar pares fadados ao score 0 — o lead volta na próxima esteira.
            if (IA.GeminiClassificador.GeracaoSuspensa) break;

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
                var r = await PontuarAsync(lead, comprador, ct);

                // A IA não conseguiu avaliar (cota estourada, erro de rede). NÃO gravamos o
                // par: um registro com score 0 vira dívida permanente — ele conta como "já
                // cruzado", então o lead nunca mais volta, e só sai da fila pelo
                // reprocessamento (poucos por dia). Foi assim que 1.133 pares ficaram
                // pendurados desde 15/06, ocupando 82,5% da Mesa. Sem gravar, o lead
                // simplesmente volta na próxima passada, quando houver cota.
                if (r.Score == 0)
                {
                    _log.LogWarning("Lead {Nome} × {Comprador}: IA indisponível — par não gravado, " +
                                    "volta na próxima rodada.", lead.RazaoSocial, comprador.Nome);
                    break; // a cota não se recupera no meio do laço; poupa as tentativas seguintes
                }

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
                : await PontuarAsync(s.Lead, comprador, ct);
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
                : await PontuarAsync(lead, s.Comprador, ct);
            AplicarResultado(s, r);
            n++;
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Lead {Nome}: {N} sinergia(s) recalculada(s) com os dados atuais", lead.RazaoSocial, n);
        return n;
    }

    /// <summary>Pontuação com dois estágios opcionais: o pente largo (flash-lite) avalia todos;
    /// finalistas (≥60) são re-pontuados pelo modelo forte quando Sinergia:DoisEstagios=true.
    /// Se o estágio preciso falhar (cota), fica o resultado do primeiro. O prompt inclui os
    /// descartes anteriores do time para o comprador (feedback loop) e, ao nascer um match
    /// quente (≥80), o time é alertado por e-mail na hora.</summary>
    private async Task<ResultadoClassificacao> PontuarAsync(Lead lead, Comprador comprador, CancellationToken ct)
    {
        var feedback = await FeedbackDoCompradorAsync(comprador.Id, lead.Id, ct);

        var r = await _ia.ClassificarSinergiaAsync(lead, comprador, feedback: feedback, ct: ct);

        // Segundo estágio: só para finalistas promissores (>= limiar) e se ainda há cota —
        // re-pontua com os modelos fortes. Poupa chamadas (a maioria dos pares nem chega lá).
        if (_doisEstagios && r.Score >= _limiarSegundoEstagio && !IA.GeminiClassificador.GeracaoSuspensa)
        {
            var refinado = await _ia.ClassificarSinergiaAsync(lead, comprador, preciso: true, feedback: feedback, ct: ct);
            if (refinado.Score > 0)
            {
                _log.LogInformation("2º estágio ({Lead} × {Comprador}): {De} → {Para} (modelo forte)",
                    lead.RazaoSocial, comprador.Nome, r.Score, refinado.Score);
                r = refinado;
            }
        }

        if (r.Score >= 80)
            await _email.EnviarMatchQuenteAsync(lead, comprador, r.Score, r.Racional, ct);

        return r;
    }

    /// <summary>Últimos descartes COM MOTIVO do time para este comprador — exemplos negativos
    /// que entram no prompt (a mesa ensina o motor). Null se não houver histórico.</summary>
    private async Task<string?> FeedbackDoCompradorAsync(int compradorId, int leadAtualId, CancellationToken ct)
    {
        var descartes = await _db.SinergiasComprador
            .Include(s => s.Lead)
            .Where(s => s.CompradorId == compradorId && s.LeadId != leadAtualId
                     && s.Status == StatusSinergia.Descartado
                     && s.MotivoDescarte != null && s.MotivoDescarte != "")
            .OrderByDescending(s => s.AtualizadoEm ?? s.GeradoEm)
            .Take(3)
            .ToListAsync(ct);

        if (descartes.Count == 0) return null;
        return string.Join("\n", descartes.Select(d =>
            $"- {d.Lead?.RazaoSocial ?? "alvo"} ({d.Lead?.Segmento ?? d.Lead?.Cnae}): {d.MotivoDescarte}"));
    }

    // Cache dos vetores de tese (deserializar 245 × 768 floats a cada cruzamento seria caro).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Hash, float[] Vetor)> _cacheVetores = new();

    /// <summary>Triagem vetorial: compara o embedding do lead com o de cada tese (cosseno) e
    /// devolve os top-N. Vazio se o lead não puder ser vetorizado ou se poucos compradores
    /// tiverem embedding atual — aí a cascata segue para a triagem por LLM.</summary>
    private async Task<List<Comprador>> ShortlistPorEmbeddingsAsync(
        Lead lead, List<Comprador> compradores, CancellationToken ct)
    {
        var comVetor = new List<(Comprador C, float[] V)>();
        foreach (var c in compradores)
        {
            if (string.IsNullOrWhiteSpace(c.TeseEmbedding) || c.TeseEmbeddingHash != HashTese(c)) continue;
            if (!_cacheVetores.TryGetValue(c.Id, out var entrada) || entrada.Hash != c.TeseEmbeddingHash)
            {
                try
                {
                    var v = System.Text.Json.JsonSerializer.Deserialize<float[]>(c.TeseEmbedding);
                    if (v is null || v.Length == 0) continue;
                    entrada = (c.TeseEmbeddingHash!, v);
                    _cacheVetores[c.Id] = entrada;
                }
                catch { continue; }
            }
            comVetor.Add((c, entrada.Vetor));
        }

        // Com poucos vetores a comparação não é representativa — melhor cair no fallback.
        if (comVetor.Count < Math.Max(10, _max))
        {
            _log.LogInformation("Triagem vetorial indisponível ({N} tese(s) com embedding) — usando fallback", comVetor.Count);
            return new();
        }

        var vetorLead = await _ia.GerarEmbeddingAsync(TextoLead(lead), ct);
        if (vetorLead is null) return new();

        return comVetor
            .Select(x => new { x.C, Sim = Cosseno(vetorLead, x.V) })
            .OrderByDescending(x => x.Sim)
            .Take(_max)
            .Select(x => x.C)
            .ToList();
    }

    /// <summary>Texto canônico da tese para o embedding (o hash detecta quando mudou).</summary>
    internal static string TextoTese(Comprador c)
    {
        var partes = new[]
        {
            c.Nome, c.TipoEmpresa, c.Segmento, c.Tags,
            c.Tese.Length > 1500 ? c.Tese[..1500] : c.Tese,
            c.ModeloNegocioAlvo, c.GeografiaAlvo,
            c.PerfilSite is { Length: > 400 } ? c.PerfilSite[..400] : c.PerfilSite
        };
        return string.Join("\n", partes.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    internal static string HashTese(Comprador c)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(TextoTese(c)));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string TextoLead(Lead l)
    {
        var partes = new[]
        {
            l.RazaoSocial, l.Segmento,
            Util.CnaeCatalogo.Descricao(l.Cnae),
            l.ModeloNegocio, l.Abrangencia, l.PorteEstimado,
            l.Descricao is { Length: > 900 } ? l.Descricao[..900] : l.Descricao,
            l.PerfilSite is { Length: > 400 } ? l.PerfilSite[..400] : l.PerfilSite
        };
        return string.Join("\n", partes.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static double Cosseno(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
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
