using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.IA;

/// <summary>Saída da IA para um candidato: nota de sinergia e racional textual.</summary>
public record ResultadoClassificacao(int Score, string Racional);

/// <summary>
/// Abstração do motor de qualificação. A IA NUNCA descobre/inventa empresas — recebe
/// um Lead REAL (já vindo do banco) e uma configuração, e devolve apenas Score+Racional.
/// Trocar de provedor (Gemini, Claude, etc.) é só registrar outra implementação no DI.
/// </summary>
public interface IClassificadorIA
{
    Task<ResultadoClassificacao> ClassificarAsync(
        Lead lead, ConfiguracaoProspeccao config, CancellationToken ct = default);
}
