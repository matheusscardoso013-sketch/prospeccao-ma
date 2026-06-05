namespace Prospeccao.Web.Models;

/// <summary>Uma linha da listagem de leads: o lead + seu melhor score (se houver).</summary>
public class LeadLinha
{
    public Lead Lead { get; set; } = default!;
    public int? MelhorScore { get; set; }
    public string? Racional { get; set; }
    public string? Fonte { get; set; }
    public DateTime? GeradoEm { get; set; }
}

/// <summary>Estado da tela de listagem (linhas + filtros aplicados).</summary>
public class LeadListaViewModel
{
    public IReadOnlyList<LeadLinha> Linhas { get; set; } = new List<LeadLinha>();
    public string? Uf { get; set; }
    public string? Situacao { get; set; }
    public string Ordenacao { get; set; } = "score"; // "score" | "razao"
}
