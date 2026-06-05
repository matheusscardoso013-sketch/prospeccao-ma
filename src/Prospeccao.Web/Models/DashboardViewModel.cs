namespace Prospeccao.Web.Models;

/// <summary>Dados resumidos do dashboard inicial.</summary>
public class DashboardViewModel
{
    public int TotalLeads { get; set; }
    public int ConfiguracoesAtivas { get; set; }
    public ExecucaoJob? UltimaExecucao { get; set; }
    public int LeadsHoje { get; set; }
}
