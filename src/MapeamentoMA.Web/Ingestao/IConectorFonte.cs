namespace MapeamentoMA.Web.Ingestao;

public record ItemIngestao(
    string TextoBruto,
    string Fonte,
    string Url,
    DateTime DataPublicacao
);

public interface IConectorFonte
{
    string Nome { get; }
    Task<IReadOnlyList<ItemIngestao>> ColetarAsync(DateTime desde, CancellationToken ct = default);
}
