using System.Globalization;
using System.Text;

namespace MapeamentoMA.Web.Ingestao;

/// <summary>
/// Consome o arquivo de Fatos Relevantes do portal de dados abertos da CVM.
/// URL configurável em Ingestao:CvmFatosUrl — padrão aponta para o CSV mensal público.
/// </summary>
public class ConectorCvm : IConectorFonte
{
    private readonly HttpClient _http;
    private readonly ILogger<ConectorCvm> _logger;
    private readonly string _url;

    public string Nome => "CVM Fatos Relevantes";

    private const string UrlDefault =
        "https://dados.cvm.gov.br/dados/CIA_ABERTA/DOC/FATOS_RELEVANTES/DADOS/fatos_relevantes_cia_aberta_atual.csv";

    public ConectorCvm(HttpClient http, IConfiguration config, ILogger<ConectorCvm> logger)
    {
        _http = http;
        _logger = logger;
        _url = config["Ingestao:CvmFatosUrl"] ?? UrlDefault;
    }

    public async Task<IReadOnlyList<ItemIngestao>> ColetarAsync(DateTime desde, CancellationToken ct = default)
    {
        var itens = new List<ItemIngestao>();
        try
        {
            var bytes = await _http.GetByteArrayAsync(_url, ct);
            // CVM usa Latin-1
            var csv = Encoding.Latin1.GetString(bytes);
            var linhas = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (linhas.Length < 2) return itens;

            // Detectar índices das colunas pelo cabeçalho
            var cabecalho = linhas[0].Split(';');
            int iData    = Array.FindIndex(cabecalho, c => c.Trim().Equals("DT_REFER", StringComparison.OrdinalIgnoreCase));
            int iAssunto = Array.FindIndex(cabecalho, c => c.Trim().Equals("DS_ASSUNTO", StringComparison.OrdinalIgnoreCase));
            int iEmpresa = Array.FindIndex(cabecalho, c => c.Trim().Equals("DENOM_CIA", StringComparison.OrdinalIgnoreCase));
            int iLink    = Array.FindIndex(cabecalho, c => c.Trim().Equals("LINK_DOC", StringComparison.OrdinalIgnoreCase));

            if (iData < 0 || iAssunto < 0) return itens;

            foreach (var linha in linhas.Skip(1))
            {
                var cols = linha.Split(';');
                if (cols.Length <= Math.Max(iData, iAssunto)) continue;

                if (!DateTime.TryParseExact(cols[iData].Trim(), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var data)) continue;
                if (data < desde) continue;

                var empresa = iEmpresa >= 0 && iEmpresa < cols.Length ? cols[iEmpresa].Trim() : "";
                var assunto = cols[iAssunto].Trim();
                var link    = iLink >= 0 && iLink < cols.Length ? cols[iLink].Trim() : _url;
                var texto   = string.IsNullOrEmpty(empresa) ? assunto : $"{empresa}: {assunto}";

                itens.Add(new ItemIngestao(texto, "CVM Fatos Relevantes", link, data));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao coletar dados CVM de {Url}", _url);
        }

        return itens;
    }
}
