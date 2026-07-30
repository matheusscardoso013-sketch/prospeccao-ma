using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

public interface IDrenoDadoRico
{
    /// <summary>Consome a cota que sobrou da rodada para enriquecer teses e perfis. Nunca lança.</summary>
    Task<(int Teses, int Perfis)> DrenarAsync(CancellationToken ct = default);
}

/// <summary>
/// O enriquecimento do dado (critérios das teses e perfis dos sites) vinha de uma tarefa
/// agendada NA MÁQUINA DO USUÁRIO, rodando os comandos de console. Isso era frágil por dois
/// motivos que se materializaram: a tarefa só dispara com o computador ligado (pulou 30/07)
/// e, em 30/07, o Smart App Control do Windows passou a bloquear binários locais — matando
/// os comandos de vez.
///
/// Agora esse trabalho roda DENTRO da rodada diária, no mesmo lugar em que a plataforma já
/// vive. Sem máquina nossa, sem segredo novo em lugar nenhum: o Render já tem a chave da IA
/// e o banco. É a última dependência de infraestrutura nossa saindo do caminho.
///
/// Usa só a cota que SOBRA: roda depois da prospecção, em lotes pequenos, e para no primeiro
/// sinal do freio de cota. Teses primeiro (alimentam o matching de todos os alvos), perfis
/// depois. Ambos são retomáveis — pulam quem já foi processado.
/// </summary>
public class DrenoDadoRico : IDrenoDadoRico
{
    private readonly AppDbContext _db;
    private readonly IClassificadorIA _ia;
    private readonly ILogger<DrenoDadoRico> _log;
    private readonly int _maxTeses;
    private readonly int _maxPerfis;

    public DrenoDadoRico(AppDbContext db, IClassificadorIA ia, ILogger<DrenoDadoRico> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _log = log;
        _maxTeses = Math.Max(0, cfg.GetValue("Prospeccao:TesesPorDia", 8));
        _maxPerfis = Math.Max(0, cfg.GetValue("Prospeccao:PerfisPorDia", 4));
    }

    public async Task<(int Teses, int Perfis)> DrenarAsync(CancellationToken ct = default)
    {
        int teses = 0, perfis = 0;
        try
        {
            teses = await EstruturarTesesAsync(ct);
            perfis = await EnriquecerPerfisAsync(ct);
        }
        catch (OperationCanceledException) { /* encerrando */ }
        catch (Exception ex)
        {
            // Enriquecimento é trabalho de fundo: nunca pode derrubar a rodada de prospecção.
            _log.LogError(ex, "Falha no dreno de dado rico (a rodada segue normalmente).");
        }
        return (teses, perfis);
    }

    private async Task<int> EstruturarTesesAsync(CancellationToken ct)
    {
        if (_maxTeses == 0 || GeminiClassificador.GeracaoSuspensa) return 0;

        var pendentes = await _db.Compradores
            .Where(c => c.Ativo && c.Tese.Length >= 20 && c.CriteriosExtraidosEm == null)
            .OrderBy(c => c.Id)
            .Take(_maxTeses)
            .ToListAsync(ct);
        if (pendentes.Count == 0) return 0;

        var feitos = 0;
        foreach (var c in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            if (GeminiClassificador.GeracaoSuspensa) break;

            var r = await _ia.ExtrairCriteriosTeseAsync(c, ct);
            if (r is null) continue; // fica pendente para a próxima rodada

            var extraidos = ComandoDadoRico.AplicarCriterios(c, r);
            await _db.SaveChangesAsync(ct);
            feitos++;
            _log.LogInformation("Tese estruturada: {Nome} ({Campos})", c.Nome,
                extraidos.Count > 0 ? string.Join("; ", extraidos) : "sem critérios explícitos");
        }

        var restam = await _db.Compradores
            .CountAsync(c => c.Ativo && c.Tese.Length >= 20 && c.CriteriosExtraidosEm == null, ct);
        _log.LogInformation("Dreno de teses: {N} estruturada(s), {Restam} na fila.", feitos, restam);
        return feitos;
    }

    private async Task<int> EnriquecerPerfisAsync(CancellationToken ct)
    {
        if (_maxPerfis == 0 || GeminiClassificador.GeracaoSuspensa) return 0;

        // Compradores primeiro: o perfil deles alimenta o matching de TODOS os alvos.
        var compradores = await _db.Compradores
            .Where(c => c.Ativo && c.Site != null && c.Site != "" && c.PerfilSiteEm == null)
            .OrderBy(c => c.Id).Take(_maxPerfis).ToListAsync(ct);

        var faltam = _maxPerfis - compradores.Count;
        var curados = faltam <= 0 ? new List<Lead>() : await _db.Leads
            .Where(l => l.Origem == Lead.OrigemValore && l.Site != null && l.Site != "" && l.PerfilSiteEm == null)
            .OrderBy(l => l.Id).Take(faltam).ToListAsync(ct);

        if (compradores.Count == 0 && curados.Count == 0) return 0;

        using var http = ComandoDadoRico.NovoNavegador();
        var feitos = 0;

        foreach (var c in compradores)
        {
            ct.ThrowIfCancellationRequested();
            if (GeminiClassificador.GeracaoSuspensa) break;
            var (perfil, motivo) = await ComandoDadoRico.PerfilDoSiteAsync(http, _ia, c.Nome, c.Site!, ct);
            c.PerfilSiteEm = DateTime.UtcNow; // marca a tentativa: site quebrado não volta todo dia
            c.PerfilSite = perfil;
            await _db.SaveChangesAsync(ct);
            if (perfil is not null) feitos++;
            else _log.LogInformation("Perfil não obtido (comprador {Nome}): {Motivo}", c.Nome, motivo);
        }

        foreach (var l in curados)
        {
            ct.ThrowIfCancellationRequested();
            if (GeminiClassificador.GeracaoSuspensa) break;
            var (perfil, motivo) = await ComandoDadoRico.PerfilDoSiteAsync(http, _ia, l.RazaoSocial, l.Site!, ct);
            l.PerfilSiteEm = DateTime.UtcNow;
            l.PerfilSite = perfil;
            await _db.SaveChangesAsync(ct);
            if (perfil is not null) feitos++;
            else _log.LogInformation("Perfil não obtido (alvo {Nome}): {Motivo}", l.RazaoSocial, motivo);
        }

        var restam = await _db.Compradores
            .CountAsync(c => c.Ativo && c.Site != null && c.Site != "" && c.PerfilSiteEm == null, ct);
        _log.LogInformation("Dreno de perfis: {N} gerado(s), {Restam} comprador(es) na fila.", feitos, restam);
        return feitos;
    }
}
