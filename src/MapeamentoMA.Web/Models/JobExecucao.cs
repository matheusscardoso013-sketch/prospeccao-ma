using System.ComponentModel.DataAnnotations;

namespace MapeamentoMA.Web.Models;

public class JobExecucao
{
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string NomeJob { get; set; } = "";

    public DateTime IniciadoEm { get; set; }
    public DateTime? ConcluidoEm { get; set; }

    public int GatilhosCriados { get; set; }
    public int AltaPrioridade { get; set; }
    public bool Sucesso { get; set; }

    [MaxLength(2000)]
    public string? ErroMensagem { get; set; }
}
