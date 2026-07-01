namespace EconomiaSim.Model;

/// <summary>
/// Banco Central. Define a Selic via uma Regra de Taylor.
/// É a principal alavanca de política do simulador.
/// </summary>
public class BancoCentral
{
    public double MetaInflacaoAnual { get; set; }   // ex.: 0.03 (3% a.a.)
    public double JuroNeutralAnual { get; set; }     // ex.: 0.045
    public double SelicAnual { get; private set; }

    // Pesos da Regra de Taylor
    public double PesoInflacao { get; set; } = 1.5;
    public double PesoHiato { get; set; } = 0.5;
    public double Suavizacao { get; set; } = 0.7; // inércia: BC move a Selic devagar
    public double TetoSelicAnual { get; set; } = 0.50; // teto da Selic (50% a.a.)

    public BancoCentral(double metaInflacaoAnual, double juroNeutralAnual, double selicInicialAnual)
    {
        MetaInflacaoAnual = metaInflacaoAnual;
        JuroNeutralAnual = juroNeutralAnual;
        SelicAnual = selicInicialAnual;
    }

    /// <summary>
    /// Calcula a nova Selic. hiato > 0 = economia aquecida (acima do potencial).
    /// </summary>
    public double DefinirSelic(double inflacaoAnualObservada, double hiatoProduto)
    {
        double alvo = JuroNeutralAnual + inflacaoAnualObservada
                    + PesoInflacao * (inflacaoAnualObservada - MetaInflacaoAnual)
                    + PesoHiato * hiatoProduto;

        // Suavização (Taylor com inércia) + piso em zero + teto plausível.
        SelicAnual = Suavizacao * SelicAnual + (1 - Suavizacao) * alvo;
        SelicAnual = Math.Clamp(SelicAnual, 0, TetoSelicAnual);
        return SelicAnual;
    }

    public void ForcarSelic(double selicAnual) => SelicAnual = Math.Max(0, selicAnual);
}
