using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Empresa REAL vinda do recorte da base pública da Receita Federal (spec seção 3).
/// Nenhum campo aqui é inventado pela IA. PorteEstimado é uma estimativa derivada de
/// capital social/CNAE e por isso seu valor exibido leva o prefixo "~".
/// </summary>
public class Lead
{
    public int Id { get; set; }

    /// <summary>CNPJ (somente dígitos). Único — base da deduplicação do job.</summary>
    [Required]
    [StringLength(14, MinimumLength = 14)]
    public string Cnpj { get; set; } = string.Empty;

    [Required]
    public string RazaoSocial { get; set; } = string.Empty;

    public string Cnae { get; set; } = string.Empty;
    public string Uf { get; set; } = string.Empty;
    public string Municipio { get; set; } = string.Empty;

    public decimal CapitalSocial { get; set; }

    /// <summary>Situação cadastral (ex.: "ATIVA").</summary>
    public string Situacao { get; set; } = string.Empty;

    /// <summary>Estimativa de porte/faturamento — sempre exibida com prefixo "~".</summary>
    public string PorteEstimado { get; set; } = string.Empty;

    /// <summary>Contato (telefone/e-mail) quando disponível no cadastro.</summary>
    public string? Contato { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public List<LeadScore> Scores { get; set; } = new();
}
