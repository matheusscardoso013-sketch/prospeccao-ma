namespace MapeamentoMA.Web.Scoring;

public class ScoringConfig
{
    public double PesoFitTese { get; set; } = 0.40;
    public double PesoFitFaturamento { get; set; } = 0.20;
    public double PesoRecencia { get; set; } = 0.30;
    public double PesoAcesso { get; set; } = 0.10;
    public double MeiaVidaRecenciaDias { get; set; } = 180;
}
