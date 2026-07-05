namespace ProspeccaoMA.Web.Models;

/// <summary>Dados do Painel (dashboard de entrada): indicadores do dia,
/// funil do pipeline e as melhores oportunidades em aberto.</summary>
public class PainelVm
{
    public string Saudacao { get; set; } = "";
    public string DataExtenso { get; set; } = "";

    public int MatchesNovosHoje { get; set; }
    public int OportunidadesQuentes { get; set; }
    public int EmNegociacao { get; set; }
    public int AlvosNaBase { get; set; }

    /// <summary>Etapas do funil na ordem do pipeline, com a contagem de cada uma.</summary>
    public List<EtapaFunil> Funil { get; set; } = new();

    public List<OportunidadePainel> Melhores { get; set; } = new();

    /// <summary>Ações agendadas para hoje ou vencidas (mini-CRM) — o que o time não pode esquecer.</summary>
    public List<AcaoPainel> Agenda { get; set; } = new();
}

public class AcaoPainel
{
    public int LeadId { get; set; }
    public string Alvo { get; set; } = "";
    public string Comprador { get; set; } = "";
    public string? Responsavel { get; set; }
    public string? Nota { get; set; }
    public DateTime Quando { get; set; }
    public bool Vencida { get; set; }
}

public class EtapaFunil
{
    public string Rotulo { get; set; } = "";
    public int Total { get; set; }
    public string Cor { get; set; } = "";     // navy | azul | ciano | teal
    public double Altura { get; set; }         // 0–100 (proporcional ao maior)
}

public class OportunidadePainel
{
    public int LeadId { get; set; }
    public string Alvo { get; set; } = "";
    public string Setor { get; set; } = "";
    public string Comprador { get; set; } = "";
    public string? Responsavel { get; set; }
    public int Score { get; set; }
    public string Racional { get; set; } = "";
}
