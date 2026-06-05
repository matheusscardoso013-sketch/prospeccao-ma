using System.Text.Json.Serialization;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Resposta da BrasilAPI (GET /api/cnpj/v1/{cnpj}). Dados REAIS de um CNPJ específico.
/// Campos mapeados conforme o JSON público da API. Nem todo campo vem preenchido.
/// </summary>
public class EmpresaBrasilApi
{
    [JsonPropertyName("cnpj")]
    public string? Cnpj { get; set; }

    [JsonPropertyName("razao_social")]
    public string? RazaoSocial { get; set; }

    [JsonPropertyName("cnae_fiscal")]
    public long? CnaeFiscal { get; set; }

    [JsonPropertyName("cnae_fiscal_descricao")]
    public string? CnaeFiscalDescricao { get; set; }

    [JsonPropertyName("uf")]
    public string? Uf { get; set; }

    [JsonPropertyName("municipio")]
    public string? Municipio { get; set; }

    [JsonPropertyName("capital_social")]
    public decimal? CapitalSocial { get; set; }

    [JsonPropertyName("descricao_situacao_cadastral")]
    public string? DescricaoSituacaoCadastral { get; set; }

    [JsonPropertyName("porte")]
    public string? Porte { get; set; }

    [JsonPropertyName("ddd_telefone_1")]
    public string? DddTelefone1 { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("logradouro")]
    public string? Logradouro { get; set; }

    [JsonPropertyName("numero")]
    public string? Numero { get; set; }

    [JsonPropertyName("bairro")]
    public string? Bairro { get; set; }

    [JsonPropertyName("cep")]
    public string? Cep { get; set; }

    [JsonPropertyName("data_inicio_atividade")]
    public string? DataInicioAtividade { get; set; }
}
