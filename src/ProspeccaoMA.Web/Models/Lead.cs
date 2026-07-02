using System.ComponentModel.DataAnnotations;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Empresa REAL. Origem: recorte da base pública da Receita Federal (spec seção 3) OU
/// a base curada da Valore (alvos sell-side da planilha, que não trazem CNPJ — por isso
/// o campo é opcional; NUNCA inventamos CNPJ). Nenhum campo aqui é gerado pela IA.
/// PorteEstimado é estimativa e leva o prefixo "~".
/// </summary>
public class Lead
{
    public const string OrigemReceita = "Receita Federal — base pública";
    public const string OrigemValore = "Base Valore (sell-side curado)";

    public int Id { get; set; }

    /// <summary>CNPJ (somente dígitos). Único quando presente — base da deduplicação do job.
    /// Nulo para alvos curados da Valore (a planilha não traz CNPJ; não inventamos).</summary>
    [StringLength(14, MinimumLength = 14)]
    public string? Cnpj { get; set; }

    [Required]
    public string RazaoSocial { get; set; } = string.Empty;

    public string Cnae { get; set; } = string.Empty;
    public string Uf { get; set; } = string.Empty;
    public string Municipio { get; set; } = string.Empty;

    public decimal CapitalSocial { get; set; }

    /// <summary>Situação cadastral (ex.: "ATIVA").</summary>
    public string Situacao { get; set; } = string.Empty;

    /// <summary>Estimativa de porte/faturamento — sempre exibida com prefixo "~".</summary>
    public string PorteEstimado { get; set; } = string.Empty;

    /// <summary>Contato (telefone/e-mail) quando disponível no cadastro.</summary>
    public string? Contato { get; set; }

    /// <summary>Origem dos dados: Receita Federal ou base curada da Valore.</summary>
    public string Origem { get; set; } = OrigemReceita;

    /// <summary>Segmento (texto livre da base curada; ex.: "Varejo e E-commerce").</summary>
    public string? Segmento { get; set; }

    /// <summary>Resumo/descrição real da empresa (base curada) — enriquece o prompt da IA.</summary>
    public string? Descricao { get; set; }

    /// <summary>Site da empresa (base curada).</summary>
    public string? Site { get; set; }

    /// <summary>Responsável Valore pelo relacionamento (base curada).</summary>
    public string? Responsavel { get; set; }

    /// <summary>Margem EBITDA declarada — estimativa, exibida com "~" (base curada).</summary>
    public string? MargemEbitda { get; set; }

    /// <summary>Valuation estimado declarado — estimativa, exibida com "~" (base curada).</summary>
    public string? ValuationEstimado { get; set; }

    /// <summary>Modelo de negócio (ex.: "SaaS B2B recorrente", "indústria sob encomenda").</summary>
    public string? ModeloNegocio { get; set; }

    /// <summary>Abrangência de atuação (local, regional, nacional, internacional).</summary>
    public string? Abrangencia { get; set; }

    /// <summary>Aspectos de cultura/gestão relevantes para fit com compradores.</summary>
    public string? Cultura { get; set; }

    /// <summary>Resumo do que a empresa faz, gerado a partir do SITE OFICIAL dela (fonte real,
    /// nunca inventado). Complementa a Descricao da planilha no prompt e na ficha.</summary>
    public string? PerfilSite { get; set; }

    public DateTime? PerfilSiteEm { get; set; }

    /// <summary>Marcado quando o usuário editou o lead à mão (rastreabilidade da fonte;
    /// reimportações preservam leads editados em vez de sobrescrevê-los).</summary>
    public bool EditadoManualmente { get; set; }

    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public List<LeadScore> Scores { get; set; } = new();
}
