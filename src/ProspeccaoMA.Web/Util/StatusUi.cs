using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Util;

/// <summary>Rótulos e cores do pipeline de matches para as telas.</summary>
public static class StatusUi
{
    public static string Rotulo(StatusSinergia s) => s switch
    {
        StatusSinergia.Novo => "Novo",
        StatusSinergia.Abordado => "Abordado",
        StatusSinergia.Reuniao => "Reunião",
        StatusSinergia.EmNegociacao => "Em negociação",
        StatusSinergia.Descartado => "Descartado",
        _ => s.ToString()
    };

    public static string Css(StatusSinergia s) => s switch
    {
        StatusSinergia.Novo => "st-novo",
        StatusSinergia.Abordado => "st-abordado",
        StatusSinergia.Reuniao => "st-reuniao",
        StatusSinergia.EmNegociacao => "st-negociacao",
        StatusSinergia.Descartado => "st-descartado",
        _ => ""
    };

    public static readonly StatusSinergia[] Todos =
    {
        StatusSinergia.Novo, StatusSinergia.Abordado, StatusSinergia.Reuniao,
        StatusSinergia.EmNegociacao, StatusSinergia.Descartado
    };
}
