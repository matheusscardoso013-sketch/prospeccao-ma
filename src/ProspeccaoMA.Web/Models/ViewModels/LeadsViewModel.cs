namespace ProspeccaoMA.Web.Models.ViewModels;

/// <summary>
/// Uma linha do dashboard. Score e RotuloScore distinguem a nota de ADERÊNCIA ao mandato
/// (lead × configuração, leads da Receita) da SINERGIA com comprador (alvos curados).
/// MelhorComprador/MelhorSinergiaScore trazem o top match de comprador quando já cruzado.
/// </summary>
public record LeadLinha(
    Lead Lead, int Score, string Racional, string Fonte, DateTime GeradoEm,
    string RotuloScore, string? MelhorComprador = null, int? MelhorSinergiaScore = null);

public enum OrdenacaoLeads { Score, Sinergia, Capital, Recente }

/// <summary>Abas da tela de Leads: a carteira curada da Valore, o pool bruto da Receita
/// e o recorte de ação (quem tem comprador quente).</summary>
public enum AbaLeads { Valore, Receita, Quentes }

public class LeadsViewModel
{
    // Filtros
    public AbaLeads Aba { get; set; } = AbaLeads.Valore;
    public string? Busca { get; set; }
    public string? Cnae { get; set; }
    public string? Uf { get; set; }
    public int? ScoreMin { get; set; }
    public OrdenacaoLeads Ordenar { get; set; } = OrdenacaoLeads.Score;

    // Paginação
    public int Pagina { get; set; } = 1;
    public const int PorPagina = 50;
    public int TotalResultados { get; set; }
    public int TotalPaginas => Math.Max(1, (int)Math.Ceiling(TotalResultados / (double)PorPagina));

    // Contagens das abas
    public int TotalValore { get; set; }
    public int TotalReceita { get; set; }
    public int TotalQuentes { get; set; }

    // Dados (só a página atual)
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
