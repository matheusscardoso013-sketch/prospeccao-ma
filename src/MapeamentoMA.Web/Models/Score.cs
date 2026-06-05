using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MapeamentoMA.Web.Models;

public class Score
{
    public int Id { get; set; }

    public int EmpresaId { get; set; }
    public Empresa Empresa { get; set; } = null!;

    public int TeseId { get; set; }
    public Tese Tese { get; set; } = null!;

    [Column(TypeName = "decimal(5,2)")]
    public decimal Valor { get; set; }

    public double CompFitTese { get; set; }
    public double CompFitFaturamento { get; set; }
    public double CompRecencia { get; set; }
    public double CompAcesso { get; set; }

    [MaxLength(200)]
    public string PesosJson { get; set; } = "";

    public DateTime CalculadoEm { get; set; } = DateTime.UtcNow;
}
