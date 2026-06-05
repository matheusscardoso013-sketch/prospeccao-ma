namespace ProspeccaoMA.Web.Models;

public enum StatusExecucao
{
    EmAndamento = 0,
    Sucesso = 1,
    Erro = 2
}

/// <summary>
/// Auditoria de cada rodada da rotina diária de prospecção (spec seção 3 e 4).
/// </summary>
public class ExecucaoJob
{
    public int Id { get; set; }

    public DateTime IniciadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? FinalizadoEm { get; set; }

    /// <summary>Quantidade de leads novos gerados nesta execução.</summary>
    public int LeadsGerados { get; set; }

    public StatusExecucao Status { get; set; } = StatusExecucao.EmAndamento;

    public string? Erro { get; set; }
}
