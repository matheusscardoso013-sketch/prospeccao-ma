using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Comprador (buy-side) da base da Valore. A "Tese" é o coração do matching: a IA
/// pontua a sinergia de um lead REAL com a tese de cada comprador. Dados vindos da
/// planilha (linhas "Buy-Side"); a IA não inventa compradores.
/// </summary>
public class Comprador
{
    public int Id { get; set; }

    [Required]
    public string Nome { get; set; } = string.Empty;

    public string? RazaoSocial { get; set; }
    public string? Contato { get; set; }
    public string? Responsavel { get; set; }

    /// <summary>Tipo de empresa (Indústria, Serviço, Tecnologia, Fundo, etc.).</summary>
    public string? TipoEmpresa { get; set; }
    public string? Segmento { get; set; }
    public string? SegmentoClientes { get; set; }
    public string? Site { get; set; }
    public string? FaixaFaturamento { get; set; }

    /// <summary>Tags da tese (ex.: #saude, #fintech, #agro).</summary>
    public string? Tags { get; set; }

    /// <summary>Tese de investimento — texto usado pela IA no matching.</summary>
    public string Tese { get; set; } = string.Empty;

    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public List<SinergiaComprador> Sinergias { get; set; } = new();
}
