using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Ingestao;
using ProspeccaoMA.Web.Matching;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Jobs;

/// <summary>
/// Rotina de prospecção (spec seção 4). A cada execução descobre e qualifica até
/// N empresas REAIS NOVAS por dia (Prospeccao:LeadsPorDia, padrão 3), middle market,
/// a partir do universo já carregado da base da Receita. Idempotente: só pega quem
/// ainda não foi pontuado (índice único LeadId+ConfiguracaoId) e respeita a faixa de
/// capital/UF/CNAE/situação de cada configuração ativa. Os escolhidos do dia são
/// enriquecidos via BrasilAPI (contatos) antes de irem para a IA. A IA nunca inventa
/// empresas — só pontua os candidatos reais.
/// Usada pelo BackgroundService das 12h e pelo botão "Rodar agora".
/// </summary>
public class RotinaProspeccao
{
    private readonly AppDbContext _db;
    private readonly IClassificadorIA _ia;
    private readonly IConectorBrasilApi _brasilApi;
    private readonly IMotorSinergia _motor;
    private readonly ILogger<RotinaProspeccao> _log;
    private readonly int _leadsPorDia;
    private readonly int _curadosPorDia;

    private const string FonteReceita = Lead.OrigemReceita;

    public RotinaProspeccao(AppDbContext db, IClassificadorIA ia, IConectorBrasilApi brasilApi,
        IMotorSinergia motor, ILogger<RotinaProspeccao> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _brasilApi = brasilApi;
        _motor = motor;
        _log = log;
        _leadsPorDia = Math.Max(1, cfg.GetValue("Prospeccao:LeadsPorDia", 3));
        _curadosPorDia = Math.Max(0, cfg.GetValue("Prospeccao:AlvosCuradosPorDia", 10));
    }

    public async Task<ExecucaoJob> ExecutarAsync(CancellationToken ct = default)
    {
        var execucao = new ExecucaoJob { IniciadoEm = DateTime.UtcNow, Status = StatusExecucao.EmAndamento };
        _db.ExecucoesJob.Add(execucao);
        await _db.SaveChangesAsync(ct);

        var totalNovos = 0;
        try
        {
            var configs = await _db.Configuracoes.Where(c => c.Ativo).ToListAsync(ct);
            _log.LogInformation("Prospecção iniciada: meta de {Meta} lead(s)/dia, {N} configuração(ões) ativa(s)",
                _leadsPorDia, configs.Count);

            // Cap GLOBAL: no máximo _leadsPorDia empresas novas por execução (no total).
            var restante = _leadsPorDia;
            foreach (var config in configs)
            {
                if (restante <= 0) break;
                ct.ThrowIfCancellationRequested();
                var n = await ProcessarConfiguracaoAsync(config, restante, ct);
                totalNovos += n;
                restante -= n;
            }

            // Esteira dos alvos curados da Valore: cruza alguns por dia com os compradores
            // (espalha a cota gratuita do Gemini em vez de processar os 117 de uma vez).
            await CruzarCuradosPendentesAsync(ct);

            execucao.LeadsGerados = totalNovos;
            execucao.Status = StatusExecucao.Sucesso;
        }
        catch (OperationCanceledException)
        {
            execucao.Status = StatusExecucao.Erro;
            execucao.Erro = "Execução cancelada.";
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Erro na rotina de prospecção");
            execucao.Status = StatusExecucao.Erro;
            execucao.Erro = ex.Message;
        }
        finally
        {
            execucao.FinalizadoEm = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation("Prospecção finalizada: {Status}, {N} nova(s) empresa(s) prospectada(s)",
            execucao.Status, execucao.LeadsGerados);
        return execucao;
    }

    private async Task<int> ProcessarConfiguracaoAsync(ConfiguracaoProspeccao config, int maximo, CancellationToken ct)
    {
        var ufs = SepararLista(config.Ufs).Select(u => u.ToUpperInvariant()).ToList();
        var cnaes = SepararLista(config.Cnaes).Select(SoDigitos).Where(c => c.Length > 0).ToList();

        // Filtro no banco: ATIVA, UF/capital (middle market), e ainda não pontuado nesta config.
        var query = _db.Leads.Where(l =>
            l.Situacao.ToUpper().Contains("ATIVA") &&
            !l.Scores.Any(s => s.ConfiguracaoId == config.Id));

        if (ufs.Count > 0)
            query = query.Where(l => ufs.Contains(l.Uf.ToUpper()));
        if (config.CapitalMin is not null)
            query = query.Where(l => l.CapitalSocial >= config.CapitalMin);
        if (config.CapitalMax is not null)
            query = query.Where(l => l.CapitalSocial <= config.CapitalMax);

        // Traz um buffer para refinar o CNAE em memória (formatos variam) e prioriza maior capital.
        var candidatos = await query
            .OrderByDescending(l => l.CapitalSocial)
            .Take(maximo * 10)
            .ToListAsync(ct);

        var selecionados = candidatos
            .Where(l => cnaes.Count == 0 || cnaes.Any(c => CnaeCombina(l.Cnae, c)))
            .Take(maximo)
            .ToList();

        _log.LogInformation("Config {Id}: {N} candidato(s) real(is) selecionado(s) (máx {Max})",
            config.Id, selecionados.Count, maximo);

        var novos = 0;
        foreach (var lead in selecionados)
        {
            ct.ThrowIfCancellationRequested();

            await EnriquecerAsync(lead, ct);              // contatos via BrasilAPI (best-effort)
            var resultado = await _ia.ClassificarAsync(lead, config, ct);

            _db.LeadScores.Add(new LeadScore
            {
                LeadId = lead.Id,
                ConfiguracaoId = config.Id,
                Score = resultado.Score,
                Racional = resultado.Racional,
                Fonte = FonteReceita,
                GeradoEm = DateTime.UtcNow
            });
            novos++;
        }

        await _db.SaveChangesAsync(ct);

        // Cruza cada novo lead com os compradores compatíveis (buy-side).
        foreach (var lead in selecionados)
        {
            ct.ThrowIfCancellationRequested();
            try { await _motor.CruzarLeadAsync(lead, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao cruzar lead {Cnpj} com compradores", lead.Cnpj); }
        }

        return novos;
    }

    /// <summary>Enriquece contatos via BrasilAPI (só os escolhidos do dia → não estoura rate limit).</summary>
    private async Task EnriquecerAsync(Lead lead, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lead.Cnpj)) return; // alvo curado sem CNPJ não consulta a BrasilAPI
        try
        {
            var dados = await _brasilApi.ConsultarAsync(lead.Cnpj, ct);
            if (dados is null) return;

            var partes = new List<string>();
            if (!string.IsNullOrWhiteSpace(dados.DddTelefone1)) partes.Add($"Tel: {dados.DddTelefone1.Trim()}");
            if (!string.IsNullOrWhiteSpace(dados.Email)) partes.Add($"E-mail: {dados.Email.Trim()}");
            var endereco = string.Join(", ", new[] { dados.Logradouro, dados.Numero, dados.Bairro, dados.Municipio, dados.Uf }
                .Where(p => !string.IsNullOrWhiteSpace(p)));
            if (!string.IsNullOrWhiteSpace(endereco)) partes.Add($"End: {endereco}");

            if (partes.Count > 0)
                lead.Contato = string.Join(" | ", partes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Enriquecimento falhou para {Cnpj} (seguindo sem contatos)", lead.Cnpj);
        }
    }

    /// <summary>Cruza até N alvos curados (Base Valore) ainda sem sinergias com os compradores.</summary>
    private async Task CruzarCuradosPendentesAsync(CancellationToken ct)
    {
        if (_curadosPorDia == 0) return;

        var pendentes = await _db.Leads
            .Where(l => l.Origem != FonteReceita && !_db.SinergiasComprador.Any(s => s.LeadId == l.Id))
            .OrderBy(l => l.Id)
            .Take(_curadosPorDia)
            .ToListAsync(ct);

        if (pendentes.Count == 0) return;
        _log.LogInformation("Esteira de alvos curados: cruzando {N} pendente(s)", pendentes.Count);

        foreach (var lead in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            try { await _motor.CruzarLeadAsync(lead, ct); }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao cruzar alvo curado {Nome}", lead.RazaoSocial); }
        }
    }

    private static List<string> SepararLista(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? new()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string SoDigitos(string s) => new(s.Where(char.IsDigit).ToArray());

    /// <summary>CNAE do lead (ex.: "6202300") casa com o filtro (ex.: "62") por igualdade ou prefixo.</summary>
    private static bool CnaeCombina(string cnaeLead, string cnaeFiltroDigitos)
    {
        var lead = SoDigitos(cnaeLead);
        if (lead.Length == 0) return false;
        return lead == cnaeFiltroDigitos || lead.StartsWith(cnaeFiltroDigitos);
    }
}
