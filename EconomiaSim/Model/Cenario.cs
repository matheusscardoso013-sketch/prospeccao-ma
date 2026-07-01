namespace EconomiaSim.Model;

/// <summary>
/// Parâmetros de um cenário de simulação. Aqui você "mexe nas alavancas".
/// </summary>
public class Cenario
{
    public int NumeroFamilias { get; set; } = 2000;
    public int Meses { get; set; } = 120; // 10 anos

    // Política monetária
    public double MetaInflacaoAnual { get; set; } = 0.03;
    public double JuroNeutralAnual { get; set; } = 0.045;
    public double SelicInicialAnual { get; set; } = 0.105; // 10,5% a.a.

    // Spread bancário (Brasil tem spread alto): crédito = Selic + spread.
    public double SpreadBancarioAnual { get; set; } = 0.25;

    // Credibilidade do BC: peso da âncora na meta ao formar expectativas (0..1).
    // Alto = expectativas ancoradas; baixo = inflação mais propensa a disparar.
    public double CredibilidadeBC { get; set; } = 0.85;

    // Indexação salarial: fração da inflação passada repassada aos salários (0..1).
    public double IndexacaoSalarial { get; set; } = 0.7;

    // Alavancas de incentivo/desincentivo (neutras por padrão). Ver Politica.cs.
    public Politica Politica { get; set; } = new();

    // Estado inicial
    public double InflacaoInicialAnual { get; set; } = 0.045;

    /// <summary>
    /// Choques exógenos por mês (índice = mês). Permite scriptar eventos:
    /// choque de oferta (ex.: alta de combustível) e Selic forçada pelo BC.
    /// </summary>
    public Func<int, ChoqueMes>? Choques { get; set; }

    public int Semente { get; set; } = 42;

    public static Cenario Padrao() => new();

    /// <summary>
    /// Cenário-exemplo: choque inflacionário no mês 12 e o BC reagindo
    /// com forte alta da Selic entre os meses 13 e 36.
    /// </summary>
    public static Cenario ChoqueESuperSelic()
    {
        var c = new Cenario();
        c.Choques = mes =>
        {
            double choqueOferta = mes is >= 12 and <= 14 ? 0.006 : 0.0; // pressão de custo
            double? selicForcada = mes is >= 13 and <= 36 ? 0.1475 : null; // BC sobe p/ 14,75%
            return new ChoqueMes(choqueOferta, selicForcada);
        };
        return c;
    }
}

/// <summary>choqueOfertaMensal: pressão de custo extra; selicForcadaAnual: sobrepõe a Regra de Taylor.</summary>
public readonly record struct ChoqueMes(double ChoqueOfertaMensal, double? SelicForcadaAnual);
