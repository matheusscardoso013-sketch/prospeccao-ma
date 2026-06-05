using System.ComponentModel.DataAnnotations;

namespace MapeamentoMA.Web.Models;

public class Gatilho
{
    public int Id { get; set; }

    public int EmpresaId { get; set; }
    public Empresa Empresa { get; set; } = null!;

    [Required, MaxLength(40)]
    public string Tipo { get; set; } = "";

    public DateOnly DataEvento { get; set; }

    public double Confianca { get; set; }

    [MaxLength(500)]
    public string Fonte { get; set; } = "";

    [MaxLength(2000)]
    public string Url { get; set; } = "";

    [StringLength(64)]
    public string HashEvento { get; set; } = "";

    public bool Revisado { get; set; }
}
