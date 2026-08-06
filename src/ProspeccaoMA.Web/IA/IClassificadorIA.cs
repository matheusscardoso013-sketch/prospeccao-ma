using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.IA;

/// <summary>Saída da IA para um candidato: nota de sinergia, racional e (quando a rubrica
/// estruturada respondeu) as subnotas — setor 0-40, porte 0-25, modelo 0-20, geo 0-15.</summary>
/// <summary>ModeloIA = qual modelo da rotação produziu este veredito. Guardado para
/// auditar cada modelo separadamente: a rotação intercala vários, e sem esse carimbo só
/// dá para afirmar que a MÉDIA está boa — não que nenhum deles está puxando para baixo.</summary>
public record ResultadoClassificacao(int Score, string Racional,
    int? Setor = null, int? Porte = null, int? Modelo = null, int? Geo = null,
    string? ModeloIA = null);

/// <summary>
/// Abstração do motor de qualificação. A IA NUNCA descobre/inventa empresas — recebe
/// um Lead REAL (já vindo do banco) e uma configuração, e devolve apenas Score+Racional.
/// Trocar de provedor (Gemini, Claude, etc.) é só registrar outra implementação no DI.
/// </summary>
public interface IClassificadorIA
{
    Task<ResultadoClassificacao> ClassificarAsync(
        Lead lead, ConfiguracaoProspeccao config, CancellationToken ct = default);

    /// <summary>
    /// Pontua a sinergia (0-100) entre um lead REAL e a TESE de investimento de um comprador
    /// (buy-side). A IA não inventa dados — só avalia o fit com base no que foi fornecido.
    /// Com <paramref name="preciso"/>, usa o modelo mais forte (Gemini:ModeloPreciso) — o
    /// segundo estágio para finalistas. <paramref name="feedback"/> traz descartes anteriores
    /// do time para este comprador (exemplos negativos — o motor aprende com a mesa).
    /// </summary>
    Task<ResultadoClassificacao> ClassificarSinergiaAsync(
        Lead lead, Comprador comprador, bool preciso = false, string? feedback = null, CancellationToken ct = default);

    /// <summary>
    /// Vetor semântico (embedding) de um texto — cota SEPARADA da geração no free tier.
    /// Null em falha (o chamador usa fallback).
    /// </summary>
    Task<float[]?> GerarEmbeddingAsync(string texto, CancellationToken ct = default);

    /// <summary>
    /// Triagem semântica: dado um lead real e a lista de compradores (com tese), devolve os
    /// ids dos mais aderentes (máx <paramref name="max"/>). Null em falha — o chamador deve
    /// usar um fallback. A IA escolhe apenas dentre os compradores listados.
    /// </summary>
    Task<List<int>?> SelecionarCompradoresAsync(
        Lead lead, IReadOnlyList<Comprador> compradores, int max, CancellationToken ct = default);

    /// <summary>
    /// Extrai da TESE (texto) os critérios estruturados EXPLÍCITOS — faixa de faturamento,
    /// margem mínima, tipo de operação, geografia, modelo, exclusões, cultura. O que não
    /// estiver explícito volta nulo (nunca presume). Null em falha.
    /// </summary>
    Task<CriteriosTese?> ExtrairCriteriosTeseAsync(Comprador comprador, CancellationToken ct = default);

    /// <summary>
    /// Resume quem é a empresa com base APENAS no texto do site oficial dela (fonte real).
    /// Devolve null em falha ou se o texto não permitir um resumo honesto.
    /// </summary>
    Task<string?> ResumirPerfilSiteAsync(string nomeEmpresa, string textoSite, CancellationToken ct = default);
}

/// <summary>Critérios estruturados extraídos de uma tese. Campos nulos = a tese não explicita.</summary>
public record CriteriosTese(
    decimal? FaturamentoMin, decimal? FaturamentoMax, decimal? MargemEbitdaMinima,
    string? TipoOperacao, string? Geografia, string? ModeloNegocio, string? Exclusoes, string? Cultura);
