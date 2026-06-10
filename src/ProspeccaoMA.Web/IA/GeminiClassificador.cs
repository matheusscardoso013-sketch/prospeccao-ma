using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.IA;

public class GeminiOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string Modelo { get; set; } = "gemini-2.5-flash";
}

/// <summary>
/// Qualificação via Google Gemini (free tier). O prompt contém APENAS dados reais do
/// candidato e instrui explicitamente a não inventar nada. A resposta é exigida em JSON
/// estrito {"score":0-100,"racional":"..."} e o parsing é protegido por try/catch.
/// </summary>
public partial class GeminiClassificador : IClassificadorIA
{
    private readonly HttpClient _http;
    private readonly ILogger<GeminiClassificador> _log;
    private readonly GeminiOptions _opt;

    private static readonly JsonSerializerOptions JsonInsensitive = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex BlocoJson();

    public GeminiClassificador(HttpClient http, ILogger<GeminiClassificador> log, IConfiguration cfg)
    {
        _http = http;
        _log = log;
        _opt = new GeminiOptions();
        cfg.GetSection("Gemini").Bind(_opt);
    }

    public async Task<ResultadoClassificacao> ClassificarAsync(
        Lead lead, ConfiguracaoProspeccao config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
        {
            _log.LogError("Gemini:ApiKey não configurada (use user-secrets). Lead {Cnpj} não pontuado.", lead.Cnpj);
            return new ResultadoClassificacao(0, "IA não configurada: defina Gemini:ApiKey para qualificar este lead.");
        }

        return await ChamarAsync(MontarPrompt(lead, config), lead.Cnpj ?? lead.RazaoSocial, ct);
    }

    public async Task<ResultadoClassificacao> ClassificarSinergiaAsync(
        Lead lead, Comprador comprador, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            return new ResultadoClassificacao(0, "IA não configurada: defina Gemini:ApiKey.");
        return await ChamarAsync(MontarPromptSinergia(lead, comprador), $"{lead.Cnpj}~{comprador.Nome}", ct);
    }

    // Espaçamento mínimo entre chamadas (free tier ~15 req/min) para evitar rajadas/429.
    private static readonly SemaphoreSlim _porta = new(1, 1);
    private static DateTime _ultima = DateTime.MinValue;
    private static readonly TimeSpan IntervaloMin = TimeSpan.FromMilliseconds(1500);
    private const int MaxTentativas = 4;

    /// <summary>Chamada genérica ao Gemini (score+racional) com parsing defensivo. Nunca lança.</summary>
    private async Task<ResultadoClassificacao> ChamarAsync(string prompt, string idLog, CancellationToken ct)
    {
        var texto = await ChamarTextoAsync(prompt, idLog, ct);
        if (texto is null)
            return new ResultadoClassificacao(0, "IA indisponível no momento (limite de uso); tente novamente.");
        return ParsearResultado(texto, idLog);
    }

    /// <summary>Chamada bruta ao Gemini (JSON estrito): devolve o texto da resposta ou null.
    /// Resiliente a rate limit (429) e erros transitórios (5xx): retry com backoff.</summary>
    private async Task<string?> ChamarTextoAsync(string prompt, string idLog, CancellationToken ct)
    {
        var corpo = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { temperature = 0.2, responseMimeType = "application/json" }
        };
        var url = $"v1beta/models/{_opt.Modelo}:generateContent?key={_opt.ApiKey}";

        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            await EspacarAsync(ct);
            try
            {
                using var req = new StringContent(JsonSerializer.Serialize(corpo), Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(url, req, ct);

                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                {
                    if (tentativa < MaxTentativas) { await BackoffAsync(tentativa, ct); continue; }
                    _log.LogWarning("Gemini rate limit/erro {Status} após {N} tentativas ({Id})", (int)resp.StatusCode, tentativa, idLog);
                    return null;
                }

                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(ct);
                return ExtrairTextoDaResposta(json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && tentativa < MaxTentativas)
            {
                _log.LogWarning(ex, "Falha transitória no Gemini (tentativa {N}, {Id})", tentativa, idLog);
                await BackoffAsync(tentativa, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha na chamada ao Gemini ({Id})", idLog);
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// Triagem semântica (1 chamada): dado o alvo e a lista de compradores com tese, a IA
    /// devolve os ids dos mais aderentes. Null em falha — o chamador usa o fallback por
    /// palavras-chave. A IA só escolhe dentre os listados (não inventa compradores).
    /// </summary>
    public async Task<List<int>?> SelecionarCompradoresAsync(
        Lead lead, IReadOnlyList<Comprador> compradores, int max, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey) || compradores.Count == 0) return null;

        var texto = await ChamarTextoAsync(MontarPromptTriagem(lead, compradores, max),
            $"triagem~{lead.RazaoSocial}", ct);
        if (string.IsNullOrWhiteSpace(texto)) return null;

        try
        {
            var bruto = texto.Trim();
            var m = BlocoJson().Match(bruto);
            if (m.Success) bruto = m.Value;

            using var doc = JsonDocument.Parse(bruto);
            if (!doc.RootElement.TryGetProperty("ids", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;

            var validos = compradores.Select(c => c.Id).ToHashSet();
            var ids = arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _))
                .Select(e => e.GetInt32())
                .Where(validos.Contains)
                .Distinct()
                .Take(max)
                .ToList();

            return ids.Count > 0 ? ids : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Triagem da IA fora do formato esperado; usando fallback. Resposta: {Texto}", texto);
            return null;
        }
    }

    private static string MontarPromptTriagem(Lead lead, IReadOnlyList<Comprador> compradores, int max)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A buy-side. Dada UMA empresa-alvo real e a lista de compradores");
        sb.AppendLine($"com suas teses, selecione os até {max} compradores com MAIOR potencial de fit (setor, porte, modelo, geografia).");
        sb.AppendLine("Regras: escolha SOMENTE ids da lista; não invente; prefira aderência de tese a fama do nome.");
        sb.AppendLine("Responda ESTRITAMENTE em JSON: {\"ids\": [<id>, <id>, ...]} — nada além disso.");
        sb.AppendLine();
        sb.AppendLine("## Empresa-alvo (dados reais)");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        if (!string.IsNullOrWhiteSpace(lead.Cnae)) sb.AppendLine($"- CNAE: {lead.Cnae}");
        if (!string.IsNullOrWhiteSpace(lead.Segmento)) sb.AppendLine($"- Segmento: {lead.Segmento}");
        if (!string.IsNullOrWhiteSpace(lead.Uf)) sb.AppendLine($"- UF: {lead.Uf}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        if (lead.CapitalSocial > 0) sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        if (!string.IsNullOrWhiteSpace(lead.Descricao)) sb.AppendLine($"- Resumo: {Resumir(lead.Descricao, 500)}");
        sb.AppendLine();
        sb.AppendLine("## Compradores (id | nome | tese | critérios)");
        foreach (var c in compradores)
        {
            var setor = string.Join("/", new[] { c.TipoEmpresa, c.Segmento }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var extras = new List<string>();
            if (c.FaturamentoMinAlvo is not null || c.FaturamentoMaxAlvo is not null)
                extras.Add($"fat. alvo {c.FaturamentoMinAlvo?.ToString("C0") ?? "até"}–{c.FaturamentoMaxAlvo?.ToString("C0") ?? "s/ teto"}");
            if (!string.IsNullOrWhiteSpace(c.Exclusoes)) extras.Add($"NÃO olha: {Resumir(c.Exclusoes, 60)}");
            var sufixo = extras.Count > 0 ? $" [{string.Join("; ", extras)}]" : "";
            sb.AppendLine($"[{c.Id}] {c.Nome}{(setor.Length > 0 ? $" ({setor})" : "")} — {Resumir(c.Tese, 200)}{sufixo}");
        }
        return sb.ToString();
    }

    private static async Task EspacarAsync(CancellationToken ct)
    {
        await _porta.WaitAsync(ct);
        try
        {
            var decorrido = DateTime.UtcNow - _ultima;
            if (decorrido < IntervaloMin) await Task.Delay(IntervaloMin - decorrido, ct);
            _ultima = DateTime.UtcNow;
        }
        finally { _porta.Release(); }
    }

    private static Task BackoffAsync(int tentativa, CancellationToken ct)
        => Task.Delay(TimeSpan.FromSeconds(2 * Math.Pow(2, tentativa)), ct); // 4s,8s,16s

    /// <summary>Prompt buy-side: fit do lead REAL com a tese do comprador. Anti-invenção.</summary>
    private static string MontarPromptSinergia(Lead lead, Comprador comprador)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A buy-side avaliando o fit entre uma empresa-alvo REAL e a TESE de investimento de um comprador.");
        sb.AppendLine("Pontue usando ESTA RUBRICA (subnotas independentes):");
        sb.AppendLine("- setor (0-40): aderência da atividade do alvo aos setores/segmentos da tese.");
        sb.AppendLine("- porte (0-25): compatibilidade do porte/ticket do alvo com a faixa buscada. Se a tese não especifica faixa, máximo 15.");
        sb.AppendLine("- modelo (0-20): fit do modelo de negócio (recorrência, B2B/B2C, serviços vs produto, contratos).");
        sb.AppendLine("- geografia (0-15): fit geográfico. Se a tese não restringe geografia, dê 10.");
        sb.AppendLine("score = setor + porte + modelo + geografia.");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- RED FLAG: se o alvo viola uma exclusão explícita da tese (ex.: 'não olham produto', 'sem muitos PJs'), o score final é no MÁXIMO 20 e o racional cita a violação.");
        sb.AppendLine("- DADOS FALTANTES: não presuma a favor — reduza a subnota correspondente e cite a lacuna no racional.");
        sb.AppendLine("- NÃO invente informações; porte/faturamento do alvo são ESTIMADOS (capital social é proxy).");
        sb.AppendLine("- Responda ESTRITAMENTE em JSON:");
        sb.AppendLine("{\"setor\":n,\"porte\":n,\"modelo\":n,\"geografia\":n,\"score\":n,\"racional\":\"<1-3 frases, terminando com a linha 'Setor n/40 · Porte n/25 · Modelo n/20 · Geo n/15'>\"}");
        sb.AppendLine();
        sb.AppendLine("## Comprador e sua tese");
        sb.AppendLine($"- Nome: {comprador.Nome}");
        if (!string.IsNullOrWhiteSpace(comprador.TipoEmpresa)) sb.AppendLine($"- Tipo: {comprador.TipoEmpresa}");
        if (!string.IsNullOrWhiteSpace(comprador.Segmento)) sb.AppendLine($"- Segmento: {comprador.Segmento}");
        if (!string.IsNullOrWhiteSpace(comprador.Tags)) sb.AppendLine($"- Tags da tese: {comprador.Tags}");
        sb.AppendLine($"- Tese: {Resumir(comprador.Tese, 1500)}");
        sb.AppendLine("### Critérios estruturados do comprador (quando informados, têm prioridade sobre o texto da tese)");
        if (comprador.FaturamentoMinAlvo is not null || comprador.FaturamentoMaxAlvo is not null)
            sb.AppendLine($"- Faixa de faturamento alvo: {(comprador.FaturamentoMinAlvo is null ? "até" : comprador.FaturamentoMinAlvo.Value.ToString("C0"))} a {(comprador.FaturamentoMaxAlvo is null ? "sem teto" : comprador.FaturamentoMaxAlvo.Value.ToString("C0"))} — pontue a subnota 'porte' comparando com o faturamento estimado do alvo.");
        else if (!string.IsNullOrWhiteSpace(comprador.FaixaFaturamento))
            sb.AppendLine($"- Faixa de faturamento alvo (texto): {comprador.FaixaFaturamento}");
        if (comprador.MargemEbitdaMinima is not null)
            sb.AppendLine($"- Margem EBITDA mínima exigida: {comprador.MargemEbitdaMinima}% — alvo abaixo disso perde pontos em 'porte'.");
        if (!string.IsNullOrWhiteSpace(comprador.TipoOperacao))
            sb.AppendLine($"- Tipo de operação buscada: {comprador.TipoOperacao}");
        if (!string.IsNullOrWhiteSpace(comprador.GeografiaAlvo))
            sb.AppendLine($"- Geografia alvo: {comprador.GeografiaAlvo}");
        if (!string.IsNullOrWhiteSpace(comprador.ModeloNegocioAlvo))
            sb.AppendLine($"- Modelo de negócio buscado: {comprador.ModeloNegocioAlvo}");
        if (!string.IsNullOrWhiteSpace(comprador.Exclusoes))
            sb.AppendLine($"- EXCLUSÕES (red flags ELIMINATÓRIAS — score máximo 20 se o alvo violar): {comprador.Exclusoes}");
        if (!string.IsNullOrWhiteSpace(comprador.Cultura))
            sb.AppendLine($"- Cultura/fit desejado: {comprador.Cultura}");
        sb.AppendLine();
        sb.AppendLine($"## Empresa-alvo (dados reais — {lead.Origem})");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        if (!string.IsNullOrWhiteSpace(lead.Cnae))
            sb.AppendLine($"- CNAE: {lead.Cnae}");
        if (!string.IsNullOrWhiteSpace(lead.Uf))
            sb.AppendLine($"- UF/Município: {lead.Uf}/{lead.Municipio}");
        if (!string.IsNullOrWhiteSpace(lead.Segmento))
            sb.AppendLine($"- Segmento: {lead.Segmento}");
        if (lead.CapitalSocial > 0)
            sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        if (!string.IsNullOrWhiteSpace(lead.Situacao))
            sb.AppendLine($"- Situação: {lead.Situacao}");
        if (!string.IsNullOrWhiteSpace(lead.MargemEbitda))
            sb.AppendLine($"- Margem EBITDA (estimada): {lead.MargemEbitda}");
        if (!string.IsNullOrWhiteSpace(lead.ValuationEstimado))
            sb.AppendLine($"- Valuation (estimado): {lead.ValuationEstimado}");
        if (!string.IsNullOrWhiteSpace(lead.ModeloNegocio))
            sb.AppendLine($"- Modelo de negócio: {lead.ModeloNegocio}");
        if (!string.IsNullOrWhiteSpace(lead.Abrangencia))
            sb.AppendLine($"- Abrangência de atuação: {lead.Abrangencia}");
        if (!string.IsNullOrWhiteSpace(lead.Cultura))
            sb.AppendLine($"- Cultura/gestão: {lead.Cultura}");
        if (!string.IsNullOrWhiteSpace(lead.Descricao))
            sb.AppendLine($"- Resumo da empresa: {Resumir(lead.Descricao, 1200)}");
        sb.AppendLine();
        sb.AppendLine("Avalie o fit (0-100) e escreva um racional curto (1-3 frases).");
        return sb.ToString();
    }

    private static string Resumir(string? s, int max)
        => string.IsNullOrWhiteSpace(s) ? "(sem tese registrada)" : (s.Length > max ? s.Substring(0, max) + "…" : s);

    /// <summary>Prompt com SOMENTE dados reais e instrução anti-invenção (spec seção 4).</summary>
    private static string MontarPrompt(Lead lead, ConfiguracaoProspeccao config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Você é um analista de M&A sell-side de uma boutique focada em MIDDLE MARKET.");
        sb.AppendLine("Avalie o potencial desta empresa-alvo REAL como candidata a uma operação de VENDA (sell-side).");
        sb.AppendLine("Pontue usando ESTA RUBRICA (subnotas independentes):");
        sb.AppendLine("- setor (0-50): aderência exata da atividade (CNAE) aos setores do mandato. Atividade genérica/adjacente vale menos.");
        sb.AppendLine("- porte (0-30): posição do capital social DENTRO da faixa do mandato (meio da faixa vale mais que as bordas; gigantes acima da faixa valem pouco).");
        sb.AppendLine("- dados (0-20): completude e qualidade dos dados disponíveis (contato, situação, clareza da atividade).");
        sb.AppendLine("score = setor + porte + dados. Use a escala inteira: candidatos medianos devem ficar em 40-70, não em 90+.");
        sb.AppendLine("Regras obrigatórias:");
        sb.AppendLine("- NÃO invente informações; porte/faturamento são ESTIMADOS (capital social é proxy).");
        sb.AppendLine("- DADOS FALTANTES: não presuma a favor; reduza a subnota e cite a lacuna.");
        sb.AppendLine("- Responda ESTRITAMENTE em JSON:");
        sb.AppendLine("{\"setor\":n,\"porte\":n,\"dados\":n,\"score\":n,\"racional\":\"<1-3 frases, terminando com a linha 'Setor n/50 · Porte n/30 · Dados n/20'>\"}");
        sb.AppendLine();
        sb.AppendLine("## Mandato (setores de interesse do cliente)");
        sb.AppendLine($"- CNAEs alvo: {config.Cnaes}");
        sb.AppendLine($"- UFs alvo: {config.Ufs}");
        if (config.CapitalMin is not null) sb.AppendLine($"- Capital social mínimo desejado: {config.CapitalMin:C}");
        if (config.CapitalMax is not null) sb.AppendLine($"- Capital social máximo desejado: {config.CapitalMax:C}");
        sb.AppendLine();
        sb.AppendLine("## Empresa-alvo (dados reais da Receita Federal)");
        sb.AppendLine($"- Razão social: {lead.RazaoSocial}");
        sb.AppendLine($"- CNPJ: {lead.Cnpj}");
        sb.AppendLine($"- CNAE: {lead.Cnae}");
        sb.AppendLine($"- UF/Município: {lead.Uf} / {lead.Municipio}");
        sb.AppendLine($"- Capital social: {lead.CapitalSocial:C}");
        sb.AppendLine($"- Situação cadastral: {lead.Situacao}");
        sb.AppendLine($"- Porte estimado: {lead.PorteEstimado}");
        sb.AppendLine();
        sb.AppendLine("Avalie a sinergia (0-100) e escreva um racional curto (1-3 frases).");
        return sb.ToString();
    }

    private static string ExtrairTextoDaResposta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }

    /// <summary>Parsing defensivo do JSON da IA. Nunca lança: degrada para um resultado seguro.</summary>
    private ResultadoClassificacao ParsearResultado(string texto, string cnpj)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return new ResultadoClassificacao(0, "Resposta vazia da IA.");

        var bruto = texto.Trim();
        // Caso o modelo envolva o JSON em ```json ... ``` ou texto extra.
        var m = BlocoJson().Match(bruto);
        if (m.Success) bruto = m.Value;

        try
        {
            using var doc = JsonDocument.Parse(bruto);
            var raiz = doc.RootElement;

            var score = 0;
            if (raiz.TryGetProperty("score", out var s))
            {
                if (s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var si)) score = si;
                else if (s.ValueKind == JsonValueKind.String && int.TryParse(s.GetString(), out var ss)) score = ss;
            }
            score = Math.Clamp(score, 0, 100);

            var racional = raiz.TryGetProperty("racional", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(racional)) racional = "Sem racional retornado pela IA.";

            return new ResultadoClassificacao(score, racional.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Não foi possível parsear o JSON da IA para {Cnpj}. Resposta: {Texto}", cnpj, texto);
            return new ResultadoClassificacao(0, "Resposta da IA fora do formato esperado; lead não pontuado.");
        }
    }
}
