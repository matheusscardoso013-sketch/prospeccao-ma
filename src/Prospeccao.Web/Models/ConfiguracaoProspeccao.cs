using System.ComponentModel.DataAnnotations;

namespace Prospeccao.Web.Models;

/// <summary>
/// Setores de interesse configurados por um usuário. O job diário usa estes
/// filtros (CNAEs, UFs, faixa de capital) para recortar o universo de CNPJs.
/// </summary>
public class ConfiguracaoProspeccao
{
    public int Id { get; set; }

    /// <summary>Id do usuário (ASP.NET Core Identity) dono desta configuração.</summary>
    [Required]
    public string UsuarioId { get; set; } = string.Empty;

    /// <summary>Lista de CNAEs-alvo, separados por vírgula. Ex.: "6201501,6202300".</summary>
    [Required]
    [Display(Name = "CNAEs (separados por vírgula)")]
    public string Cnaes { get; set; } = string.Empty;

    /// <summary>Lista de UFs-alvo, separadas por vírgula. Ex.: "SP,MG,PR".</summary>
    [Required]
    [Display(Name = "UFs (separadas por vírgula)")]
    public string Ufs { get; set; } = string.Empty;

    /// <summary>Capital social mínimo do recorte (R$).</summary>
    [Display(Name = "Capital social mínimo")]
    public decimal? CapitalMin { get; set; }

    /// <summary>Capital social máximo do recorte (R$).</summary>
    [Display(Name = "Capital social máximo")]
    public decimal? CapitalMax { get; set; }

    /// <summary>Liga/desliga a configuração sem apagá-la. Só as ativas entram no job.</summary>
    [Display(Name = "Ativa")]
    public bool Ativo { get; set; } = true;

    /// <summary>Scores gerados para esta configuração.</summary>
    public ICollection<LeadScore> Scores { get; set; } = new List<LeadScore>();
}
