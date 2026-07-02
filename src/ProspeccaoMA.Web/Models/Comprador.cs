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

    // ----- Critérios estruturados da tese (deixam o matching da IA mais preciso) -----

    /// <summary>Faturamento anual mínimo buscado no alvo (R$).</summary>
    public decimal? FaturamentoMinAlvo { get; set; }

    /// <summary>Faturamento anual máximo buscado no alvo (R$).</summary>
    public decimal? FaturamentoMaxAlvo { get; set; }

    /// <summary>Margem EBITDA mínima exigida (%).</summary>
    public decimal? MargemEbitdaMinima { get; set; }

    /// <summary>Tipo de operação buscada (Controle, Minoritária, 100%, Indiferente).</summary>
    public string? TipoOperacao { get; set; }

    /// <summary>Geografia alvo (ex.: "Nacional", "Sudeste", "SP e PR").</summary>
    public string? GeografiaAlvo { get; set; }

    /// <summary>Modelo de negócio buscado (ex.: "B2B com receita recorrente; serviços").</summary>
    public string? ModeloNegocioAlvo { get; set; }

    /// <summary>O que o comprador NÃO olha — red flags eliminatórias no matching.</summary>
    public string? Exclusoes { get; set; }

    /// <summary>Aspectos de cultura/fit desejados (ex.: "fundador permanece; gestão profissionalizada").</summary>
    public string? Cultura { get; set; }

    // ----- Dado rico (Onda 3) -----

    /// <summary>Quando os critérios estruturados foram extraídos automaticamente da tese pela IA
    /// (nulo = nunca extraídos). A extração só preenche campos vazios, nunca sobrescreve.</summary>
    public DateTime? CriteriosExtraidosEm { get; set; }

    /// <summary>Time revisou/confirmou os critérios extraídos pela IA.</summary>
    public bool CriteriosValidados { get; set; }

    /// <summary>Resumo de quem é o comprador, gerado a partir do SITE OFICIAL (fonte real,
    /// nunca inventado). Alimenta o prompt de matching e a ficha.</summary>
    public string? PerfilSite { get; set; }

    public DateTime? PerfilSiteEm { get; set; }

    public bool Ativo { get; set; } = true;
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public List<SinergiaComprador> Sinergias { get; set; } = new();
}
