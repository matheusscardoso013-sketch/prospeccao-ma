using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Setores de interesse configuráveis por usuário (spec seção 3).
/// Cnaes e Ufs são listas separadas por vírgula (ex.: "6422100,6499900" / "DF,SP").
/// O job diário usa estes filtros para recortar o universo de CNPJs reais.
/// </summary>
public class ConfiguracaoProspeccao
{
    public int Id { get; set; }

    [Required]
    public string UsuarioId { get; set; } = string.Empty;
    public Usuario? Usuario { get; set; }

    /// <summary>CNAEs alvo, separados por vírgula. Ex.: "6422100,6499900".</summary>
    [Required]
    public string Cnaes { get; set; } = string.Empty;

    /// <summary>UFs alvo, separadas por vírgula. Ex.: "DF,SP,MG".</summary>
    [Required]
    public string Ufs { get; set; } = string.Empty;

    public decimal? CapitalMin { get; set; }
    public decimal? CapitalMax { get; set; }

    public bool Ativo { get; set; } = true;

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
}
