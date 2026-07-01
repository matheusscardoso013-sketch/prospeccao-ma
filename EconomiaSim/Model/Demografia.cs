namespace EconomiaSim.Model;

/// <summary>
/// Dinâmica demográfica — o que faz a sociedade VIVER. Cada família tem, por mês,
/// probabilidades de NASCIMENTO (formar novo domicílio), MORTE (dissolução) e de
/// MOBILIDADE social (subir/descer de classe), todas dependentes do estado
/// econômico. É por aqui que as variáveis viram crescimento ou declínio da
/// população — e da cidade.
/// </summary>
public static class Demografia
{
    // Taxas-base MENSAIS por classe (A..E). Classes mais baixas têm fertilidade
    // maior (padrão real); ~1-2,5% a.a. em condições normais.
    private static readonly double[] NascimentoBase = { 0.0010, 0.0012, 0.0014, 0.0018, 0.0022 };
    private const double MorteBase = 0.0006; // ~0,7% a.a. em condições normais

    /// <summary>
    /// Probabilidade de a família formar um novo domicílio neste mês.
    /// <paramref name="prosperidade"/> = renda real / renda de referência da classe
    /// (1 = na média da classe). Renda alta e emprego incentivam filhos.
    /// </summary>
    public static double ProbNascimento(Familia f, double inflacaoAnual, double prosperidade)
    {
        double fatorEmprego = f.Empregado ? 1.0 : 0.4;
        double fatorInflacao = Math.Clamp(1.2 - 2.0 * Math.Max(0, inflacaoAnual - 0.06), 0.3, 1.2);
        double fatorRenda = Math.Clamp(prosperidade, 0.4, 1.8); // prosperidade incentiva natalidade
        return NascimentoBase[(int)f.Classe] * fatorEmprego * fatorInflacao * fatorRenda;
    }

    /// <summary>
    /// Probabilidade de morte/dissolução neste mês (sobe com a privação).
    /// <paramref name="fatorSaude"/> &lt; 1 = boa infraestrutura de saúde (Fase 3)
    /// reduz a mortalidade.
    /// </summary>
    public static double ProbMorte(Familia f, double patrimonioReal, double prosperidade, double fatorSaude)
    {
        double fator = 1.0
            + (f.Empregado ? 0.0 : 0.8)              // desemprego
            + (patrimonioReal < 0 ? 0.5 : 0.0)        // endividamento
            + f.MesesDesempregado * 0.02              // desemprego prolongado ("desespero")
            + (prosperidade < 0.7 ? (0.7 - prosperidade) * 1.5 : 0.0); // renda baixa eleva mortalidade
        return MorteBase * Math.Clamp(fator, 0.5, 6.0) * fatorSaude;
    }

    /// <summary>
    /// Direção desejada de mobilidade social: -1 sobe (rumo a A), +1 desce (rumo a E),
    /// 0 fica. A aplicação é probabilística (ver Simulacao).
    /// </summary>
    public static int DirecaoMobilidade(Familia f, double patrimonioReal)
    {
        int c = (int)f.Classe;
        double rendaBase = ClasseRendaInfo.RendaBase(f.Classe);
        // Sobe: acumulou patrimônio real alto e está empregada.
        if (c > 0 && f.Empregado && patrimonioReal > rendaBase * 24) return -1;
        // Desce: dívida pesada ou desemprego longo.
        if (c < 4 && (patrimonioReal < -rendaBase * 6 || f.MesesDesempregado > 18)) return +1;
        return 0;
    }
}
