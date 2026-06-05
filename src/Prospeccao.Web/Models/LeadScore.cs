using System.ComponentModel.DataAnnotations;

namespace Prospeccao.Web.Models;

/// <summary>
/// Saída da qualificação (futuro job de IA) para um par Lead × Configuração.
/// Fonte rastreia a origem do dado ("Receita Federal — base pública").
/// </summary>
public class LeadScore
{
    public int Id { get; set; }

    public int LeadId { get; set; }
    public Lead? Lead { get; set; }

    public int ConfiguracaoId { get; set; }
    public ConfiguracaoProspeccao? Configuracao { get; set; }

    /// <summary>Nota de sinergia de 0 a 100.</summary>
    [Range(0, 100)]
    public int Score { get; set; }

    /// <summary>Racional textual curto do porquê do score.</summary>
    public string? Racional { get; set; }

    /// <summary>Origem rastreável do dado/estimativa.</summary>
    [Required]
    public string Fonte { get; set; } = string.Empty;

    [Display(Name = "Gerado em")]
    public DateTime GeradoEm { get; set; }
}
