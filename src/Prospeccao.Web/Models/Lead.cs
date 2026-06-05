using System.ComponentModel.DataAnnotations;

namespace Prospeccao.Web.Models;

/// <summary>
/// Empresa real vinda do recorte da base pública da Receita Federal.
/// O Claude NUNCA cria Leads — apenas pontua os que já existem aqui.
/// CNPJ é a chave natural de deduplicação.
/// </summary>
public class Lead
{
    public int Id { get; set; }

    /// <summary>CNPJ somente dígitos (14). Único — evita repetir empresas.</summary>
    [Required]
    [StringLength(14, MinimumLength = 14)]
    public string Cnpj { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Razão social")]
    public string RazaoSocial { get; set; } = string.Empty;

    /// <summary>CNAE principal (7 dígitos). Indexado para o fit de tese.</summary>
    [Display(Name = "CNAE")]
    public string? Cnae { get; set; }

    public string? Uf { get; set; }

    [Display(Name = "Município")]
    public string? Municipio { get; set; }

    /// <summary>Capital social declarado (R$).</summary>
    [Display(Name = "Capital social")]
    public decimal? CapitalSocial { get; set; }

    /// <summary>Situação cadastral (ex.: "ATIVA").</summary>
    [Display(Name = "Situação")]
    public string? Situacao { get; set; }

    /// <summary>
    /// Porte estimado a partir de capital/CNAE/porte declarado. É ESTIMATIVA:
    /// exibir sempre com prefixo "~" e Fonte preenchida no LeadScore.
    /// </summary>
    [Display(Name = "Porte estimado")]
    public string? PorteEstimado { get; set; }

    /// <summary>Contato (telefone/e-mail) quando houver no cadastro.</summary>
    public string? Contato { get; set; }

    /// <summary>Scores atribuídos a este lead.</summary>
    public ICollection<LeadScore> Scores { get; set; } = new List<LeadScore>();
}
