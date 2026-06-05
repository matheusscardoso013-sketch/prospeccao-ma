using System.Text;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Utilitários para ler os CSVs públicos da Receita Federal (layout oficial):
/// separador ';', todo campo entre aspas duplas, encoding Latin1 (ISO-8859-1),
/// sem cabeçalho, decimal com vírgula.
/// </summary>
public static class CsvReceita
{
    public static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

    /// <summary>Parser de uma linha CSV (delimitador ';', aspas '"') tolerante a aspas internas.</summary>
    public static string[] ParseLinha(string linha)
    {
        var campos = new List<string>();
        var sb = new StringBuilder();
        var emAspas = false;

        for (var i = 0; i < linha.Length; i++)
        {
            var c = linha[i];
            if (c == '"')
            {
                if (emAspas && i + 1 < linha.Length && linha[i + 1] == '"') { sb.Append('"'); i++; }
                else emAspas = !emAspas;
            }
            else if (c == ';' && !emAspas)
            {
                campos.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        campos.Add(sb.ToString());
        return campos.ToArray();
    }

    /// <summary>Capital social no formato "6000000000,00" → decimal.</summary>
    public static decimal ParseCapital(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return 0m;
        var normal = bruto.Trim().Replace(".", "").Replace(",", ".");
        return decimal.TryParse(normal, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    public static string SoDigitos(string? s) => new((s ?? string.Empty).Where(char.IsDigit).ToArray());
}
