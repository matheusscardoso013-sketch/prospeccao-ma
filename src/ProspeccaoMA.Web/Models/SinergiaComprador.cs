using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>Estágio de trabalho de um match no pipeline do time.</summary>
public enum StatusSinergia
{
    Novo = 0,
    Abordado = 1,
    Reuniao = 2,
    EmNegociacao = 3,
    Descartado = 4
}

/// <summary>
/// Cruzamento alvo × comprador: a IA pontua (0-100) a sinergia entre um lead REAL e a
/// tese de investimento de um comprador, com um racional. Idempotente por (Lead, Comprador).
/// O time trabalha o match pelo pipeline (Status + Anotacoes) — base futura do feedback à IA.
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

    /// <summary>Estágio no pipeline de trabalho (Novo → Abordado → Reunião → Em negociação / Descartado).</summary>
    public StatusSinergia Status { get; set; } = StatusSinergia.Novo;

    /// <summary>Anotações do time sobre a abordagem (feedback que no futuro alimenta a IA).</summary>
    public string? Anotacoes { get; set; }

    public DateTime? AtualizadoEm { get; set; }
}
