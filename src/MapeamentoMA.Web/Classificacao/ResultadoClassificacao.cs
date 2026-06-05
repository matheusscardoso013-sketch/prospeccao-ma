namespace MapeamentoMA.Web.Classificacao;

public class ResultadoClassificacao
{
    public string? Empresa { get; set; }
    public string? Cnpj { get; set; }
    public string? TeseSugerida { get; set; }
    public bool TemGatilho { get; set; }
    public string? TipoGatilho { get; set; }
    public decimal ValorEnvolvido { get; set; }
    public bool ValorEstimado { get; set; }
    public double Confianca { get; set; }

    public bool NaoClassificado { get; set; }
    public string? TextoBrutoPreservado { get; set; }
}
