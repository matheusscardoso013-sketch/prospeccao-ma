using Prospeccao.Web.Models;

namespace Prospeccao.Web.IA;

/// <summary>
/// Camada de qualificação por IA atrás de uma interface, para trocar o backend
/// (Ollama local hoje) sem alterar o resto do sistema.
/// </summary>
public interface IClassificadorIA
{
    /// <summary>
    /// Pontua a sinergia do <paramref name="lead"/> com a <paramref name="config"/>,
    /// usando SOMENTE os dados reais fornecidos. Nunca inventa informação.
    /// </summary>
    Task<ResultadoQualificacao> QualificarAsync(
        Lead lead, ConfiguracaoProspeccao config, CancellationToken ct = default);
}
