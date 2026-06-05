namespace ProspeccaoMA.Web.Models.ViewModels;

/// <summary>Uma linha do dashboard: o lead real + seu melhor score (entre as configs).</summary>
public record LeadLinha(Lead Lead, int Score, string Racional, string Fonte, DateTime GeradoEm);

public enum OrdenacaoLeads { Score, Capital, Recente }

public class LeadsViewModel
{
    // Filtros
    public string? Cnae { get; set; }
    public string? Uf { get; set; }
    public int? ScoreMin { get; set; }
    public OrdenacaoLeads Ordenar { get; set; } = OrdenacaoLeads.Score;

    // Dados
    public List<LeadLinha> Leads { get; set; } = new();

    // KPIs
    public int TotalLeads { get; set; }
    public int GeradosHoje { get; set; }
    public int ScoreMedio { get; set; }
    public ExecucaoJob? UltimaExecucao { get; set; }

    // Opções de filtro
    public List<string> Ufs { get; set; } = new();
    public List<string> Cnaes { get; set; } = new();
}
