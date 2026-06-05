using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Saída da IA para um lead real, no contexto de uma configuração (spec seção 3).
/// A IA NUNCA cria empresas: apenas pontua (Score 0-100) e redige o Racional sobre
/// dados reais. Fonte rastreia a origem dos dados (ex.: "Receita Federal — base pública").
/// </summary>
public class LeadScore
{
    public int Id { get; set; }

    public int LeadId { get; set; }
    public Lead? Lead { get; set; }

    public int ConfiguracaoId { get; set; }
    public ConfiguracaoProspeccao? Configuracao { get; set; }

    /// <summary>Sinergia 0-100 atribuída pela IA.</summary>
    [Range(0, 100)]
    public int Score { get; set; }

    /// <summary>Racional textual curto redigido pela IA com base nos dados reais.</summary>
    public string Racional { get; set; } = string.Empty;

    /// <summary>Origem dos dados do lead. Ex.: "Receita Federal — base pública".</summary>
    public string Fonte { get; set; } = string.Empty;

    public DateTime GeradoEm { get; set; } = DateTime.UtcNow;
}
