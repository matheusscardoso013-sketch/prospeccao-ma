using System.ComponentModel.DataAnnotations;

namespace Prospeccao.Web.Models;

/// <summary>Auditoria de cada execução da rotina diária de prospecção (12h).</summary>
public class ExecucaoJob
{
    public int Id { get; set; }

    [Display(Name = "Iniciado em")]
    public DateTime IniciadoEm { get; set; }

    [Display(Name = "Finalizado em")]
    public DateTime? FinalizadoEm { get; set; }

    /// <summary>Quantidade de leads gerados nesta execução.</summary>
    [Display(Name = "Leads gerados")]
    public int LeadsGerados { get; set; }

    /// <summary>Status da execução (ex.: "Sucesso", "Erro", "EmAndamento").</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Mensagem de erro, quando houver.</summary>
    public string? Erro { get; set; }
}
