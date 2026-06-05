using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MapeamentoMA.Web.Models;

public class Tese
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nome { get; set; } = "";

    public string CnaesAlvo { get; set; } = "";

    [Column(TypeName = "decimal(18,2)")]
    public decimal FaturamentoMin { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FaturamentoMax { get; set; }

    public string Racional { get; set; } = "";

    public string PerfilComprador { get; set; } = "";

    public bool Ativa { get; set; } = true;

    public ICollection<Empresa> Empresas { get; set; } = [];
}
