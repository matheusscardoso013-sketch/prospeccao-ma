using System.ServiceModel.Syndication;
using System.Xml;

namespace MapeamentoMA.Web.Ingestao;

public class ConectorRssNoticias : IConectorFonte
{
    private readonly HttpClient _http;
    private readonly ILogger<ConectorRssNoticias> _logger;
    private readonly IReadOnlyList<string> _feeds;

    public string Nome => "RSS Notícias";

    public ConectorRssNoticias(HttpClient http, IConfiguration config, ILogger<ConectorRssNoticias> logger)
    {
        _http = http;
        _logger = logger;
        _feeds = config.GetSection("Ingestao:FeedsRss").Get<string[]>() ?? FeedsDefault;
    }

    private static readonly string[] FeedsDefault =
    [
        "https://www.infomoney.com.br/feed/",
        "https://valoreconomico.globo.com/rss/financas/",
        "https://exame.com/rss/",
    ];

    public async Task<IReadOnlyList<ItemIngestao>> ColetarAsync(DateTime desde, CancellationToken ct = default)
    {
        var itens = new List<ItemIngestao>();

        foreach (var feedUrl in _feeds)
        {
            try
            {
                var xml = await _http.GetStringAsync(feedUrl, ct);
                using var reader = XmlReader.Create(new StringReader(xml));
                var feed = SyndicationFeed.Load(reader);

                foreach (var item in feed.Items)
                {
                    var pub = item.PublishDate.UtcDateTime;
                    if (pub < desde) continue;

                    var titulo = item.Title?.Text ?? "";
                    var resumo = item.Summary?.Text ?? "";
                    var texto = $"{titulo}. {resumo}".Trim();
                    var url = item.Links.FirstOrDefault()?.Uri.ToString() ?? feedUrl;

                    itens.Add(new ItemIngestao(texto, $"RSS: {feed.Title?.Text ?? feedUrl}", url, pub));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao coletar feed RSS {Feed}", feedUrl);
            }
        }

        return itens;
    }
}
