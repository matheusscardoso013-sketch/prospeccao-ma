namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Estima porte a partir de dados REAIS (capital social + porte declarado na Receita).
/// É uma ESTIMATIVA — o resultado vem sempre com prefixo "~". A base pública não tem
/// faturamento/EBITDA reais de empresa fechada; por isso não inventamos números, apenas
/// classificamos em faixas transparentes derivadas do que existe.
/// </summary>
public static class EstimadorPorte
{
    public static string Estimar(decimal capitalSocial, string? porteDeclarado)
    {
        var porte = (porteDeclarado ?? string.Empty).Trim().ToUpperInvariant();

        // Porte declarado pela própria Receita tem prioridade.
        if (porte.Contains("MICRO") || porte == "01")
            return "~ Microempresa";
        if (porte.Contains("PEQUENO") || porte == "03")
            return "~ Pequeno porte";

        // "DEMAIS" (05) ou não informado: aproxima pela faixa de capital social.
        return capitalSocial switch
        {
            >= 50_000_000m => "~ Grande porte",
            >= 10_000_000m => "~ Médio porte",
            >= 1_000_000m => "~ Pequeno/médio porte",
            > 0m => "~ Pequeno porte",
            _ => "~ Porte indeterminado"
        };
    }
}
