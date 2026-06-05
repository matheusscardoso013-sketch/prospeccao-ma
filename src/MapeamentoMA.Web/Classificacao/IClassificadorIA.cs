namespace MapeamentoMA.Web.Classificacao;

public interface IClassificadorIA
{
    Task<ResultadoClassificacao> ClassificarAsync(string textoBruto, CancellationToken ct = default);
}
