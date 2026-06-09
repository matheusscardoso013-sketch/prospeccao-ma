using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

public record ResultadoImportCompradores(int LinhasBuySide, int Novos, int Atualizados);

public interface IImportadorCompradores
{
    Task<ResultadoImportCompradores> ImportarAsync(string caminhoXlsx, CancellationToken ct = default);
}

/// <summary>
/// Importa a base buy-side da Valore (planilha .xlsx, aba BaseDadosEmpresas, linhas
/// "Buy-Side") para a tabela Compradores. Idempotente por Nome (deduplica a planilha).
/// Lê o .xlsx via XML cru (System.IO.Compression) — tolera células enormes (transcrições)
/// que estouram o limite de bibliotecas como o ClosedXML.
/// Mapeia: A=Nome, B=Interesse, D=RazaoSocial, E=Contato, F=Responsavel, G=Tipo,
/// H=Segmento, I=SegmentoClientes, J=Site, K=FaixaFaturamento, O=Tags, P=Tese.
/// </summary>
public class ImportadorCompradores : IImportadorCompradores
{
    private readonly AppDbContext _db;
    private readonly ILogger<ImportadorCompradores> _log;
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public ImportadorCompradores(AppDbContext db, ILogger<ImportadorCompradores> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<ResultadoImportCompradores> ImportarAsync(string caminhoXlsx, CancellationToken ct = default)
    {
        if (!File.Exists(caminhoXlsx))
            throw new FileNotFoundException($"Planilha não encontrada: {caminhoXlsx}");

        using var zip = ZipFile.OpenRead(caminhoXlsx);

        var strings = LerSharedStrings(zip);
        var caminhoAba = MapearAba(zip, "BaseDados");

        var linhasBuy = 0;
        var porNome = new Dictionary<string, Comprador>(StringComparer.OrdinalIgnoreCase);

        using (var s = zip.GetEntry(caminhoAba)!.Open())
        {
            var doc = XDocument.Load(s);
            var sheetData = doc.Root!.Element(Ns + "sheetData");
            if (sheetData is null) throw new InvalidOperationException("Aba sem sheetData.");

            foreach (var row in sheetData.Elements(Ns + "row"))
            {
                var cels = LerLinha(row, strings);
                if (!cels.TryGetValue("B", out var interesse) || !interesse.Contains("Buy", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!cels.TryGetValue("A", out var nome) || string.IsNullOrWhiteSpace(nome))
                    continue;

                linhasBuy++;
                porNome[nome.Trim()] = new Comprador
                {
                    Nome = nome.Trim(),
                    RazaoSocial = Get(cels, "D"),
                    Contato = Get(cels, "E"),
                    Responsavel = Get(cels, "F"),
                    TipoEmpresa = Get(cels, "G"),
                    Segmento = Get(cels, "H"),
                    SegmentoClientes = Get(cels, "I"),
                    Site = Get(cels, "J"),
                    FaixaFaturamento = Get(cels, "K"),
                    Tags = Get(cels, "O"),
                    Tese = cels.TryGetValue("P", out var p) ? p.Trim() : string.Empty
                };
            }
        }

        int novos = 0, atualizados = 0;
        foreach (var (nome, novo) in porNome)
        {
            ct.ThrowIfCancellationRequested();
            var existente = await _db.Compradores.FirstOrDefaultAsync(c => c.Nome == nome, ct);
            if (existente is null)
            {
                _db.Compradores.Add(novo);
                novos++;
            }
            else
            {
                existente.RazaoSocial = novo.RazaoSocial;
                existente.Contato = novo.Contato;
                existente.Responsavel = novo.Responsavel;
                existente.TipoEmpresa = novo.TipoEmpresa;
                existente.Segmento = novo.Segmento;
                existente.SegmentoClientes = novo.SegmentoClientes;
                existente.Site = novo.Site;
                existente.FaixaFaturamento = novo.FaixaFaturamento;
                existente.Tags = novo.Tags;
                existente.Tese = novo.Tese;
                atualizados++;
            }
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Compradores importados: {Novos} novo(s), {Atu} atualizado(s) de {Linhas} linha(s) buy-side",
            novos, atualizados, linhasBuy);
        return new ResultadoImportCompradores(linhasBuy, novos, atualizados);
    }

    private static List<string> LerSharedStrings(ZipArchive zip)
    {
        var lista = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e is null) return lista;
        using var s = e.Open();
        var doc = XDocument.Load(s);
        foreach (var si in doc.Root!.Elements(Ns + "si"))
            lista.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));
        return lista;
    }

    /// <summary>Acha o arquivo da aba cujo nome contém o termo (ex.: "BaseDados").</summary>
    private static string MapearAba(ZipArchive zip, string termo)
    {
        using var wbS = zip.GetEntry("xl/workbook.xml")!.Open();
        var wb = XDocument.Load(wbS);
        var sheet = wb.Root!.Element(Ns + "sheets")!.Elements(Ns + "sheet")
            .FirstOrDefault(x => (x.Attribute("name")?.Value ?? "").Contains(termo, StringComparison.OrdinalIgnoreCase))
            ?? wb.Root!.Element(Ns + "sheets")!.Elements(Ns + "sheet").First();
        var rid = sheet.Attribute(NsR + "id")!.Value;

        using var relS = zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open();
        var rels = XDocument.Load(relS);
        XNamespace nsp = "http://schemas.openxmlformats.org/package/2006/relationships";
        var target = rels.Root!.Elements(nsp + "Relationship")
            .First(r => r.Attribute("Id")!.Value == rid).Attribute("Target")!.Value;
        return "xl/" + target.TrimStart('/');
    }

    private static Dictionary<string, string> LerLinha(XElement row, List<string> strings)
    {
        var d = new Dictionary<string, string>();
        foreach (var c in row.Elements(Ns + "c"))
        {
            var refCol = new string((c.Attribute("r")?.Value ?? "").Where(char.IsLetter).ToArray());
            if (refCol.Length == 0) continue;
            var t = c.Attribute("t")?.Value;
            string val;
            if (t == "s")
            {
                var v = c.Element(Ns + "v")?.Value;
                val = (int.TryParse(v, out var idx) && idx >= 0 && idx < strings.Count) ? strings[idx] : "";
            }
            else if (t == "inlineStr")
            {
                val = string.Concat(c.Descendants(Ns + "t").Select(x => x.Value));
            }
            else
            {
                val = c.Element(Ns + "v")?.Value ?? "";
            }
            d[refCol] = val;
        }
        return d;
    }

    private static string? Get(Dictionary<string, string> cels, string col)
        => cels.TryGetValue(col, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
}
