using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MapeamentoMA.Web.Models;

public class Empresa
{
    public int Id { get; set; }

    [Required, StringLength(14, MinimumLength = 14)]
    public string Cnpj { get; set; } = "";

    [Required, MaxLength(200)]
    public string RazaoSocial { get; set; } = "";

    [StringLength(7)]
    public string CnaePrincipal { get; set; } = "";

    [MaxLength(40)]
    public string Porte { get; set; } = "";

    [MaxLength(40)]
    public string SituacaoCadastral { get; set; } = "";

    public DateOnly? DataAbertura { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? FaturamentoEstimado { get; set; }

    public bool IndicadorEstimado { get; set; }

    public int? TeseId { get; set; }
    public Tese? Tese { get; set; }

    [MaxLength(200)]
    public string FonteOrigem { get; set; } = "";

    public DateTime DataColeta { get; set; } = DateTime.UtcNow;

    public ICollection<Gatilho> Gatilhos { get; set; } = [];
    public ICollection<Score> Scores { get; set; } = [];
    public PipelineItem? PipelineItem { get; set; }
}
