using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Cruzamento alvo × comprador: a IA pontua (0-100) a sinergia entre um lead REAL e a
/// tese de investimento de um comprador, com um racional. Idempotente por (Lead, Comprador).
/// </summary>
public class SinergiaComprador
{
    public int Id { get; set; }

    public int LeadId { get; set; }
    public Lead? Lead { get; set; }

    public int CompradorId { get; set; }
    public Comprador? Comprador { get; set; }

    [Range(0, 100)]
    public int Score { get; set; }

    public string Racional { get; set; } = string.Empty;

    public DateTime GeradoEm { get; set; } = DateTime.UtcNow;
}
