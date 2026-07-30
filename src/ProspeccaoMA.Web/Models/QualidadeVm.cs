namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Auditoria da qualidade do matching. Nasceu como comando de console, virou tela quando o
/// Smart App Control passou a bloquear binários locais — e ficou melhor assim: o time vê
/// sozinho se a IA está calibrada, sem depender de ninguém rodar comando.
/// </summary>
public class QualidadeVm
{
    public int TotalPares { get; set; }
    public int PareAvaliados { get; set; }
    public int AguardandoAvaliacao { get; set; }

    public List<FaixaScore> Distribuicao { get; set; } = new();
    public List<FaixaTese> PorProfundidadeTese { get; set; } = new();
    public List<ConcentracaoComprador> Concentracao { get; set; } = new();
}

public class FaixaScore
{
    public string Rotulo { get; set; } = "";
    public int Qtd { get; set; }
    public double Pct { get; set; }
}

/// <summary>
/// O teste da hipótese: tese rasa deixa a IA generosa (sem critério concreto para reprovar,
/// ela preenche a lacuna com otimismo). Se for verdade, as faixas de tese curta terão score
/// médio e taxa de "quente" MAIORES que as de tese longa — o contrário do esperado.
/// </summary>
public class FaixaTese
{
    public string Rotulo { get; set; } = "";
    public int Compradores { get; set; }
    public int Pares { get; set; }
    public double ScoreMedio { get; set; }
    public double PctQuentes { get; set; }
}

public class ConcentracaoComprador
{
    public string Nome { get; set; } = "";
    public int Quentes { get; set; }
    public int TamanhoTese { get; set; }
    public double ScoreMedio { get; set; }
}
