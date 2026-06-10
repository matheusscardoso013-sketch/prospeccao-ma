using System.IO.Compression;
using System.Xml.Linq;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Leitor de .xlsx via XML cru (sem ClosedXML, que estoura em células &gt;32k como as
/// atas/transcrições da base Valore). Devolve cada linha como dicionário coluna→valor.
/// </summary>
public static class XlsxLeitor
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace NsR = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Lê as linhas da aba cujo nome contém <paramref name="termoAba"/> (a partir da linha 2).</summary>
    public static List<Dictionary<string, string>> LerLinhas(string caminhoXlsx, string termoAba)
    {
        using var zip = ZipFile.OpenRead(caminhoXlsx);

        var strings = LerSharedStrings(zip);
        var caminhoAba = MapearAba(zip, termoAba);

        var linhas = new List<Dictionary<string, string>>();
        using var s = zip.GetEntry(caminhoAba)!.Open();
        var doc = XDocument.Load(s);
        var sheetData = doc.Root!.Element(Ns + "sheetData")
            ?? throw new InvalidOperationException("Aba sem sheetData.");

        foreach (var row in sheetData.Elements(Ns + "row"))
        {
            if (row.Attribute("r")?.Value == "1") continue; // cabeçalho
            linhas.Add(LerLinha(row, strings));
        }
        return linhas;
    }

    public static string? Get(Dictionary<string, string> cels, string col)
        => cels.TryGetValue(col, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

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
}
