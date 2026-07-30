using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Util;

/// <summary>
/// Quem entra no cruzamento com os alvos.
///
/// O critério era só "tese com 20+ caracteres", repetido em quatro lugares. Isso deixava de
/// fora compradores que a Valore conhece bem e cujo site descreve com precisão o que eles
/// são — a tese não estar digitada não significa que não sabemos nada sobre eles. Em 30/07
/// eram 21 compradores ativos parados assim, sem receber um único lead.
///
/// Agora vale a tese OU o perfil extraído do SITE OFICIAL (texto real, nunca inventado).
/// Como o dreno de dado rico enriquece perfis a cada rodada, esses compradores entram no
/// jogo sozinhos, conforme o perfil é obtido — sem ninguém digitar nada.
///
/// Também tratamos "0.0" e "0" como ausência de tese: são lixo que veio da planilha de
/// origem e apareciam na tela como se fossem a tese do comprador.
/// </summary>
public static class Teses
{
    public const int MinimoUtil = 20;

    /// <summary>A tese está preenchida de verdade (não é vazia, curta demais ou lixo "0.0").</summary>
    public static bool TemTeseUtil(Comprador c)
        => c.Tese.Length >= MinimoUtil && c.Tese != "0.0" && c.Tese != "0";

    /// <summary>Dá para cruzar este comprador com alvos: tem tese OU perfil do site.</summary>
    public static bool EhCruzavel(Comprador c)
        => TemTeseUtil(c) || !string.IsNullOrWhiteSpace(c.PerfilSite);

    public static IQueryable<Comprador> ComTeseUtil(this IQueryable<Comprador> q)
        => q.Where(c => c.Tese.Length >= MinimoUtil && c.Tese != "0.0" && c.Tese != "0");

    public static IQueryable<Comprador> Cruzaveis(this IQueryable<Comprador> q)
        => q.Where(c => (c.Tese.Length >= MinimoUtil && c.Tese != "0.0" && c.Tese != "0")
                     || (c.PerfilSite != null && c.PerfilSite != ""));

    public static IQueryable<Comprador> ForaDoCruzamento(this IQueryable<Comprador> q)
        => q.Where(c => !(c.Tese.Length >= MinimoUtil && c.Tese != "0.0" && c.Tese != "0")
                     && (c.PerfilSite == null || c.PerfilSite == ""));
}
