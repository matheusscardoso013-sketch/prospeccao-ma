using System.ComponentModel.DataAnnotations;

namespace MapeamentoMA.Web.Models;

public class PipelineItem
{
    public int Id { get; set; }

    public int EmpresaId { get; set; }
    public Empresa Empresa { get; set; } = null!;

    [Required, MaxLength(20)]
    public string Estagio { get; set; } = "Identificado";

    public int Ordem { get; set; }

    public DateTime AtualizadoEm { get; set; } = DateTime.UtcNow;

    public string? AtualizadoPorUsuarioId { get; set; }
}
