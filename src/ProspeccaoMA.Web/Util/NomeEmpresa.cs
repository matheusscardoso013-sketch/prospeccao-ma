using System.Globalization;
using System.Text;

namespace ProspeccaoMA.Web.Util;

/// <summary>
/// Nome de empresa em forma comparável: sem acento, sem pontuação, sem sufixo societário e
/// em caixa alta. "Magazord", "MAGAZORD LTDA." e "Magazord S/A" viram a mesma chave.
///
/// Existe porque a mesma empresa aparecia dos dois lados da mesa — cadastrada como alvo e
/// como compradora — e o motor gerava um par dela consigo mesma (visto em 30/07: Magazord
/// score 90, Twins Software 95, ocupando vaga de oportunidade quente).
/// </summary>
public static class NomeEmpresa
{
    private static readonly string[] Sufixos =
    {
        "LTDA", "SA", "S A", "EIRELI", "ME", "EPP", "MEI", "S S", "SS",
        "PARTICIPACOES", "HOLDING", "GROUP", "GRUPO"
    };

    public static string Chave(string? nome)
    {
        if (string.IsNullOrWhiteSpace(nome)) return "";

        var semAcento = new string(nome.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToUpperInvariant();

        var limpo = new string(semAcento.Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray());

        var palavras = limpo.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (palavras.Count > 1 && Sufixos.Contains(palavras[^1])) palavras.RemoveAt(palavras.Count - 1);

        return string.Join(' ', palavras);
    }

    /// <summary>Alvo e comprador são a MESMA empresa? Compara contra o nome e a razão social
    /// do comprador — vários entram na base com um nome curto e a razão social completa.</summary>
    public static bool MesmaEmpresa(string? razaoAlvo, string? nomeComprador, string? razaoComprador)
    {
        var alvo = Chave(razaoAlvo);
        if (alvo.Length < 4) return false;
        return alvo == Chave(nomeComprador) || alvo == Chave(razaoComprador);
    }
}
