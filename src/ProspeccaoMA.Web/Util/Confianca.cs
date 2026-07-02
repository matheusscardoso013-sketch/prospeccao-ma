using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Util;

/// <summary>
/// Selo de confiança do DADO de um alvo: quanto mais rico o insumo, mais confiável o score
/// que a IA produz sobre ele. Alvo com descrição/perfil real ≠ alvo só com código CNAE.
/// </summary>
public static class Confianca
{
    public static string Nivel(Lead l)
    {
        var temHistoria = !string.IsNullOrWhiteSpace(l.Descricao) || !string.IsNullOrWhiteSpace(l.PerfilSite);
        var temContexto = !string.IsNullOrWhiteSpace(l.Segmento) || !string.IsNullOrWhiteSpace(l.ModeloNegocio);
        if (temHistoria && temContexto) return "alta";
        if (temHistoria || temContexto) return "média";
        return "baixa";
    }

    public static string Css(Lead l) => Nivel(l) switch
    {
        "alta" => "cf-alta",
        "média" => "cf-media",
        _ => "cf-baixa"
    };
}
