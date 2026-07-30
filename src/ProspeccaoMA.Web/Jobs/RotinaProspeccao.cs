using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Ingestao;
using ProspeccaoMA.Web.Matching;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Notificacoes;

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
    private readonly INotificadorEmail _email;
    private readonly Ingestao.IDrenoDadoRico _dreno;
    private readonly ILogger<RotinaProspeccao> _log;
    private readonly int _leadsPorDia;
    private readonly int _curadosPorDia;
    private readonly int _reprocessarPorDia;

    private const string FonteReceita = Lead.OrigemReceita;

    public RotinaProspeccao(AppDbContext db, IClassificadorIA ia, IConectorBrasilApi brasilApi,
        IMotorSinergia motor, INotificadorEmail email, Ingestao.IDrenoDadoRico dreno,
        ILogger<RotinaProspeccao> log, IConfiguration cfg)
    {
        _db = db;
        _ia = ia;
        _brasilApi = brasilApi;
        _motor = motor;
        _email = email;
        _dreno = dreno;
        _log = log;
        _leadsPorDia = Math.Max(1, cfg.GetValue("Prospeccao:LeadsPorDia", 3));
        _curadosPorDia = Math.Max(0, cfg.GetValue("Prospeccao:AlvosCuradosPorDia", 10));
        _reprocessarPorDia = Math.Max(0, cfg.GetValue("Prospeccao:ReprocessarFalhasPorDia", 30));
    }

    // Trava global de execução única: impede rodadas sobrepostas (timer interno + cron
    // disparando juntos, ou disparo manual durante uma rodada) que competiriam pela cota da IA.
    private static readonly SemaphoreSlim _execucaoUnica = new(1, 1);

    public async Task<ExecucaoJob> ExecutarAsync(CancellationToken ct = default)
    {
        if (!await _execucaoUnica.WaitAsync(0, ct))
        {
            _log.LogInformation("Já há uma rodada em andamento — disparo ignorado.");
            return new ExecucaoJob { IniciadoEm = DateTime.UtcNow, FinalizadoEm = DateTime.UtcNow,
                Status = StatusExecucao.Erro, Erro = "Ignorada: já havia uma rodada em andamento." };
        }
        try
        {
            return await ExecutarInternoAsync(ct);
        }
        finally { _execucaoUnica.Release(); }
    }

    /// <summary>Marca como Erro as execuções que ficaram "EmAndamento" (servidor hibernou/reiniciou
    /// no meio). Roda no início de qualquer disparo, mantendo o painel de Execuções limpo.</summary>
    private async Task HigienizarOrfasAsync(CancellationToken ct)
    {
        var limite = DateTime.UtcNow.AddHours(-2);
        var orfas = await _db.ExecucoesJob
            .Where(e => e.Status == StatusExecucao.EmAndamento && e.IniciadoEm < limite)
            .ToListAsync(ct);
        foreach (var o in orfas)
        {
            o.Status = StatusExecucao.Erro;
            o.Erro = "Interrompida (servidor reiniciou durante a execução).";
            o.FinalizadoEm = o.FinalizadoEm ?? DateTime.UtcNow;
        }
        if (orfas.Count > 0) await _db.SaveChangesAsync(ct);
    }

    private async Task<ExecucaoJob> ExecutarInternoAsync(CancellationToken ct)
    {
        await HigienizarOrfasAsync(ct);

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

            // NOTA: o backfill dos alvos curados NÃO roda mais aqui — ele é dirigido pelo
            // cron externo (/Jobs/Disparar?modo=curados), em fatias pequenas ao longo do dia.
            // Assim a rodada diária fica curta (só os leads do dia) e sobrevive à hibernação
            // do free tier, em vez de tentar processar tudo num bloco longo que morria no meio.

            // Auto-cura: reavalia um punhado de pontuações que falharam (rate limit transitório).
            await ReprocessarFalhasInternoAsync(_reprocessarPorDia, ct);

            // Enriquecimento do dado (teses + perfis) com a cota que sobrou. Antes isso vinha de
            // uma tarefa agendada na máquina do usuário — que parou de funcionar quando o
            // Smart App Control bloqueou binários locais. Agora mora aqui, onde a plataforma
            // já vive, e não depende de máquina nenhuma nossa.
            var (tesesOk, perfisOk) = await _dreno.DrenarAsync(ct);
            if (tesesOk + perfisOk > 0)
                _log.LogInformation("Dado rico: {T} tese(s) e {P} perfil(is) enriquecidos nesta rodada.", tesesOk, perfisOk);

            // Resumo diário por e-mail (leads do dia + melhores matches). Nunca derruba a rotina.
            await _email.EnviarResumoDiarioAsync(ct);

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

    /// <summary>Cruza até N alvos curados (Base Valore) ainda sem sinergias com os compradores.
    /// Quando a fila esvaziar, vira no-op — e o regime passa a ser só os leads novos do dia.</summary>
    /// <summary>Versão pública (endpoint): respeita a trava de execução única.</summary>
    public async Task<int> CruzarCuradosPendentesAsync(int? max = null, CancellationToken ct = default)
    {
        if (!await _execucaoUnica.WaitAsync(0, ct))
        {
            _log.LogInformation("Backfill ignorado — já há uma rodada em andamento.");
            return 0;
        }
        try { await HigienizarOrfasAsync(ct); return await CruzarCuradosInternoAsync(max, ct); }
        finally { _execucaoUnica.Release(); }
    }

    private async Task<int> CruzarCuradosInternoAsync(int? max = null, CancellationToken ct = default)
    {
        var lote = max ?? _curadosPorDia;
        if (lote <= 0) return 0;

        var pendentes = await _db.Leads
            .Where(l => l.Origem != FonteReceita && !_db.SinergiasComprador.Any(s => s.LeadId == l.Id))
            .OrderBy(l => l.Id)
            .Take(lote)
            .ToListAsync(ct);

        if (pendentes.Count == 0) return 0;
        _log.LogInformation("Esteira de alvos curados: cruzando {N} pendente(s)", pendentes.Count);

        var ok = 0;
        foreach (var lead in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            try { await _motor.CruzarLeadAsync(lead, ct); ok++; }
            catch (Exception ex) { _log.LogWarning(ex, "Falha ao cruzar alvo curado {Nome}", lead.RazaoSocial); }
        }
        return ok;
    }

    /// <summary>
    /// Ordena a fila de reavaliação pelo cosseno entre o embedding do alvo e o da tese do
    /// comprador — quem tem mais cara de match vai primeiro. Embeddings têm cota própria
    /// (bem mais folgada que a de geração) e ficam gravados, então o custo é pago uma vez.
    /// Quem não tiver vetor de um dos lados vai para o fim da fila, na ordem antiga, em vez
    /// de ficar de fora: prioridade é melhoria, não requisito.
    /// </summary>
    /// <summary>Espia a fila de reavaliação já priorizada, SEM reavaliar nada (não gasta cota
    /// de geração — só de embedding, que é separada). Serve ao comando de console "fila".</summary>
    public async Task<List<Models.SinergiaComprador>> PreverFilaAsync(int quantos, CancellationToken ct = default)
    {
        var pendentes = (await _db.SinergiasComprador
                .Include(s => s.Lead).Include(s => s.Comprador)
                .Where(s => s.Score == 0)
                .ToListAsync(ct))
            .Where(s => MarcasFalha.Any(m => s.Racional.StartsWith(m)))
            .ToList();
        return await PriorizarPorPotencialAsync(pendentes, quantos, ct);
    }

    private async Task<List<Models.SinergiaComprador>> PriorizarPorPotencialAsync(
        List<Models.SinergiaComprador> pendentes, int quantos, CancellationToken ct)
    {
        // A priorização é um GANHO, não um requisito: se qualquer coisa der errado aqui
        // (embedding fora do ar, vetor corrompido), a rodada do dia não pode cair junto —
        // caímos na ordem antiga, que é o comportamento que já existia.
        try
        {
            return await OrdenarPorSimilaridadeAsync(pendentes, quantos, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não consegui priorizar a fila de reavaliação — seguindo na ordem padrão.");
            return pendentes.Take(quantos).ToList();
        }
    }

    private async Task<List<Models.SinergiaComprador>> OrdenarPorSimilaridadeAsync(
        List<Models.SinergiaComprador> pendentes, int quantos, CancellationToken ct)
    {
        if (pendentes.Count <= quantos) return pendentes;

        var pontuados = new List<(Models.SinergiaComprador S, double Sim)>();
        var semVetor = new List<Models.SinergiaComprador>();

        // Só geramos embedding para os alvos que ainda não têm — no 1º dia isso custa algumas
        // dezenas de chamadas de EMBEDDING (não de geração); depois já vem tudo do banco.
        var orcamentoEmbeddings = 60;

        foreach (var s in pendentes)
        {
            ct.ThrowIfCancellationRequested();
            if (s.Lead is null || s.Comprador is null) { semVetor.Add(s); continue; }

            var teseJson = s.Comprador.TeseEmbedding;
            if (string.IsNullOrWhiteSpace(teseJson)) { semVetor.Add(s); continue; }

            var temVetorSalvo = s.Lead.TextoEmbedding is not null
                                && s.Lead.TextoEmbeddingHash == Matching.MotorSinergia.HashLead(s.Lead);
            if (!temVetorSalvo && orcamentoEmbeddings <= 0) { semVetor.Add(s); continue; }

            var vetorLead = await _motor.EmbeddingDoLeadAsync(s.Lead, ct);
            if (!temVetorSalvo) orcamentoEmbeddings--;
            if (vetorLead is null) { semVetor.Add(s); continue; }

            try
            {
                var vetorTese = System.Text.Json.JsonSerializer.Deserialize<float[]>(teseJson);
                if (vetorTese is null || vetorTese.Length == 0) { semVetor.Add(s); continue; }
                pontuados.Add((s, Matching.MotorSinergia.Similaridade(vetorLead, vetorTese)));
            }
            catch { semVetor.Add(s); }
        }

        await _db.SaveChangesAsync(ct); // guarda os embeddings recém-gerados

        var fila = pontuados.OrderByDescending(x => x.Sim).Select(x => x.S).Concat(semVetor).ToList();
        if (pontuados.Count > 0)
            _log.LogInformation("Fila de reavaliação priorizada: {N} par(es) com similaridade " +
                                "(melhor {Max:0.00}), {Sem} sem vetor no fim.",
                pontuados.Count, pontuados.Max(x => x.Sim), semVetor.Count);

        return fila.Take(quantos).ToList();
    }

    private static readonly string[] MarcasFalha =
    {
        "Falha ao contatar a IA",
        "IA indisponível",
        "Resposta da IA fora do formato",
        "Resposta vazia da IA",
        "IA não configurada"
    };

    /// <summary>
    /// Reavalia pontuações que ficaram com falha (score 0 por rate limit/erro transitório),
    /// tanto LeadScores (lead × configuração) quanto SinergiasComprador (lead × comprador).
    /// </summary>
    /// <summary>Versão pública (endpoint): respeita a trava de execução única.</summary>
    public async Task<(int Leads, int Sinergias)> ReprocessarFalhasAsync(int max, CancellationToken ct = default)
    {
        if (!await _execucaoUnica.WaitAsync(0, ct))
        {
            _log.LogInformation("Reprocessamento ignorado — já há uma rodada em andamento.");
            return (0, 0);
        }
        try { await HigienizarOrfasAsync(ct); return await ReprocessarFalhasInternoAsync(max, ct); }
        finally { _execucaoUnica.Release(); }
    }

    private async Task<(int Leads, int Sinergias)> ReprocessarFalhasInternoAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) return (0, 0);
        int leadsOk = 0, sinergiasOk = 0;

        var scoresFalha = (await _db.LeadScores
                .Include(s => s.Lead)
                .Where(s => s.Score == 0)
                .ToListAsync(ct))
            .Where(s => MarcasFalha.Any(m => s.Racional.StartsWith(m)))
            .Take(max)
            .ToList();

        foreach (var s in scoresFalha)
        {
            ct.ThrowIfCancellationRequested();
            if (IA.GeminiClassificador.GeracaoSuspensa) break; // cota morta — não queimar retries
            if (s.Lead is null) continue;
            var config = await _db.Configuracoes.FirstOrDefaultAsync(c => c.Id == s.ConfiguracaoId, ct);
            if (config is null) continue;

            var r = await _ia.ClassificarAsync(s.Lead, config, ct);
            s.Score = r.Score;
            s.Racional = r.Racional;
            s.GeradoEm = DateTime.UtcNow;
            leadsOk++;
        }

        var restante = max - leadsOk;
        if (restante > 0)
        {
            var pendentes = (await _db.SinergiasComprador
                    .Include(s => s.Lead).Include(s => s.Comprador)
                    .Where(s => s.Score == 0)
                    .ToListAsync(ct))
                .Where(s => MarcasFalha.Any(m => s.Racional.StartsWith(m)))
                .ToList();

            // Ordem por POTENCIAL, não por ordem de chegada. A fila de reavaliação é grande
            // (1.133 pares em 30/07) e cabem poucos por dia na cota gratuita — pegar os
            // primeiros da lista significava sortear. A similaridade alvo × tese vem de
            // embeddings, cuja cota é SEPARADA da geração, então ranquear sai de graça e os
            // pares mais promissores chegam à Mesa em dias, não em meses.
            var sinFalha = (await PriorizarPorPotencialAsync(pendentes, restante, ct));

            foreach (var s in sinFalha)
            {
                ct.ThrowIfCancellationRequested();
                if (IA.GeminiClassificador.GeracaoSuspensa) break; // cota morta — não queimar retries
                if (s.Lead is null || s.Comprador is null) continue;

                var r = await _ia.ClassificarSinergiaAsync(s.Lead, s.Comprador, ct: ct);
                Matching.MotorSinergia.AplicarResultado(s, r);
                sinergiasOk++;
            }
        }

        if (leadsOk + sinergiasOk > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Reprocessamento de falhas: {L} lead score(s) e {S} sinergia(s) reavaliado(s)",
                leadsOk, sinergiasOk);
        }
        return (leadsOk, sinergiasOk);
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
