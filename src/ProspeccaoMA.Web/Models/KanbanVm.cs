namespace ProspeccaoMA.Web.Models;

/// <summary>Quadro Kanban do pipeline: uma coluna por status, com os melhores cartões
/// de cada etapa. O arrastar-e-soltar move o match entre colunas (salva o status).</summary>
public class KanbanVm
{
    public int? ScoreMin { get; set; }
    public string? Busca { get; set; }
    public string? Resp { get; set; }
    public List<string> Responsaveis { get; set; } = new();
    public List<KanbanColuna> Colunas { get; set; } = new();
}

public class KanbanColuna
{
    public StatusSinergia Status { get; set; }
    public string Rotulo { get; set; } = "";
    public string Css { get; set; } = "";
    public int Total { get; set; }
    public List<SinergiaComprador> Cards { get; set; } = new();
}
