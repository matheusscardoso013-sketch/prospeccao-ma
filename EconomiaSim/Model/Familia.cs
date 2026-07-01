namespace EconomiaSim.Model;

/// <summary>
/// Agente família (household). Heterogêneo por classe de renda.
/// Cada família trabalha, recebe renda, paga/recebe juros, forma expectativa
/// de inflação e decide quanto consumir vs. poupar.
/// </summary>
public class Familia
{
    public int Id { get; }
    public ClasseRenda Classe { get; set; }      // muda com a mobilidade social

    public bool Empregado { get; set; } = true;
    public int MesesDesempregado { get; set; }    // duração do desemprego (mortalidade/queda de classe)
    public double Salario { get; set; }          // renda mensal do trabalho
    public double Patrimonio { get; set; }        // poupança (>0) ou dívida (<0)
    public double ExpectativaInflacao { get; set; } // mensal esperada

    // Propensão a consumir acompanha a classe atual (importa para a mobilidade).
    public double PropensaoConsumo => ClasseRendaInfo.PropensaoConsumo(Classe);

    // Métricas do último período (para relatórios distributivos)
    public double UltimoConsumo { get; set; }
    public double UltimaRendaDisponivel { get; set; }

    public Familia(int id, ClasseRenda classe, double inflacaoInicial)
    {
        Id = id;
        Classe = classe;
        Salario = ClasseRendaInfo.RendaBase(classe);
        Patrimonio = ClasseRendaInfo.PatrimonioInicialEmMeses(classe) * Salario;
        ExpectativaInflacao = inflacaoInicial;
    }

    /// <summary>
    /// Atualiza expectativa de inflação. Mistura um componente ADAPTATIVO
    /// (inflação observada vs. expectativa anterior) com uma ÂNCORA na meta do BC.
    /// A credibilidade do BC define o peso da âncora — é o que impede a
    /// expectativa de disparar num feedback explosivo (hiperinflação).
    /// </summary>
    public void AtualizarExpectativa(double inflacaoObservadaMensal, double metaMensal,
                                     double credibilidade = 0.5, double pesoObservado = 0.6)
    {
        double adaptativa = pesoObservado * inflacaoObservadaMensal
                          + (1 - pesoObservado) * ExpectativaInflacao;
        ExpectativaInflacao = credibilidade * metaMensal
                            + (1 - credibilidade) * adaptativa;
    }

    /// <summary>
    /// Decide consumo do mês e atualiza patrimônio.
    /// Aqui mora o efeito distributivo da Selic:
    ///  - poupadores (ricos) GANHAM com juros altos;
    ///  - endividados (pobres) PERDEM renda disponível com o serviço da dívida.
    /// </summary>
    public void DecidirConsumoEPoupanca(double taxaPoupancaMensal, double taxaCreditoMensal,
                                        Politica pol, double salarioMinimoAtual)
    {
        // Piso salarial (incentivo de renda) aplicado a quem está abaixo do mínimo.
        double salarioEfetivo = Math.Max(Salario, salarioMinimoAtual);
        double rendaTrabalho = Empregado ? salarioEfetivo : salarioEfetivo * 0.30; // seguro/informal

        // Imposto de renda PROGRESSIVO: incide mais sobre as classes altas.
        rendaTrabalho *= 1 - pol.IRProgressivo * Politica.PesoIR(Classe);
        // Transferência (renda básica / Bolsa Família) à base da pirâmide.
        rendaTrabalho += pol.TransferenciaMensal * Politica.PesoTransferencia(Classe);

        double taxaNominal = Patrimonio >= 0 ? taxaPoupancaMensal : taxaCreditoMensal;
        double jurosNominais = Patrimonio * taxaNominal;
        // Juros REAIS (descontando a inflação esperada) é o que o agente percebe
        // como ganho/perda de poder de compra ao DECIDIR consumo. Usar juros
        // nominais aqui geraria um espiral (juro alto -> renda -> consumo -> inflação).
        double jurosReais = Patrimonio * (taxaNominal - ExpectativaInflacao);

        // Renda que baliza a decisão de consumo (em termos reais).
        double rendaParaConsumo = rendaTrabalho + jurosReais;
        UltimaRendaDisponivel = rendaParaConsumo;

        // Efeito SUBSTITUIÇÃO: juro real alto -> adia consumo (poupa mais).
        // É o canal pelo qual a Selic ESFRIA a demanda. Mais forte nos ricos.
        double juroRealEsperado = taxaPoupancaMensal - ExpectativaInflacao;
        double sensibilidade = 1.0 - (int)Classe * 0.12; // A mais sensível que E
        double ajuste = 1.0 - Math.Clamp(juroRealEsperado * 12.0 * sensibilidade, -0.30, 0.40);

        // MPC sobre renda do TRABALHO é alta; sobre renda de JUROS é BEM baixa
        // (poupadores reinvestem quase tudo). Mantê-la baixa evita que o juro alto
        // vire um windfall de consumo dos ricos que (a) inverte o sinal da política
        // e (b) polui a leitura causal dos experimentos fiscais.
        const double mpcJuros = 0.08;
        double consumo = Math.Max(0,
            (rendaTrabalho * PropensaoConsumo + Math.Max(0, jurosReais) * mpcJuros) * ajuste);

        // Famílias pobres têm consumo de subsistência: não conseguem cortar abaixo de um piso.
        double piso = ClasseRendaInfo.RendaBase(Classe) * 0.55;
        if (Classe is ClasseRenda.D or ClasseRenda.E)
            consumo = Math.Max(consumo, Math.Min(piso, rendaTrabalho));

        // Imposto sobre consumo: do que a família gasta, parte é tributo e só o
        // resto vira BENS. Desincentiva o consumo e pesa mais sobre quem gasta
        // tudo (classes baixas). A demanda real às empresas são os bens recebidos.
        double bensRecebidos = consumo / (1 + pol.ImpostoConsumo);
        UltimoConsumo = bensRecebidos;

        // O ESTOQUE de patrimônio acumula a juros NOMINAIS (preserva o valor real
        // quando o juro nominal acompanha a inflação) menos o gasto total (com imposto).
        Patrimonio += rendaTrabalho + jurosNominais - consumo;
    }
}
