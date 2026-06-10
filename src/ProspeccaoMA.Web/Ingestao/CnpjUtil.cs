using System.Text.RegularExpressions;

namespace ProspeccaoMA.Web.Ingestao;

public static partial class CnpjUtil
{
    [GeneratedRegex(@"\D")]
    private static partial Regex SomenteDigitos();

    /// <summary>Remove tudo que não é dígito e valida 14 posições. Retorna null se inválido.</summary>
    public static string? Normalizar(string? bruto)
    {
        if (string.IsNullOrWhiteSpace(bruto)) return null;
        var limpo = SomenteDigitos().Replace(bruto, string.Empty);
        return limpo.Length == 14 ? limpo : null;
    }

    /// <summary>Formata 14 dígitos como 00.000.000/0000-00 (apenas para exibição).
    /// Nulo/vazio (alvos curados sem CNPJ) vira string vazia.</summary>
    public static string Formatar(string? cnpj14)
        => string.IsNullOrWhiteSpace(cnpj14)
            ? string.Empty
            : cnpj14.Length != 14
                ? cnpj14
                : $"{cnpj14[..2]}.{cnpj14[2..5]}.{cnpj14[5..8]}/{cnpj14[8..12]}-{cnpj14[12..]}";
}
