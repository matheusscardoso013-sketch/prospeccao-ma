namespace EconomiaSim.Model;

/// <summary>
/// Setor produtivo como POPULAÇÃO de empresas (Fase 2). A capacidade total é a
/// soma das capacidades das firmas. Firmas crescem por investimento (margem
/// intensiva), NASCEM quando há lucro e FECHAM na crise (margem extensiva). A
/// produção gera os empregos (ver Simulacao). Define também a inflação (Phillips).
/// </summary>
public class SetorProdutivo
{
    private readonly List<Empresa> _empresas = new();
    private readonly Random _rng;
    private double _capacidadeAlvo; // caminho suave da capacidade (estável); firmas o seguem

    public IReadOnlyList<Empresa> Empresas => _empresas;
    public int NumEmpresas => _empresas.Count;
    public double ProdutoPotencial => _empresas.Sum(e => e.Capacidade); // soma das firmas
    public double ProdutoEfetivo { get; private set; }
    public double NivelPrecos { get; private set; } = 1.0;
    public double InflacaoMensal { get; private set; }

    // Curva de Phillips do lado da oferta
    public double RepassePhillips { get; set; } = 0.15;
    public double InerciaInflacao { get; set; } = 0.6;

    // Ganho de produtividade do trabalho (reajuste real de salários).
    public double CrescimentoTendencialMensal { get; set; } = 0.0020;

    // ---- Canal de investimento (margem intensiva: firmas existentes crescem) ----
    public double InvestAutonomo { get; set; } = 0.20;
    public double Acelerador { get; set; } = 0.5;
    public double SensibJuro { get; set; } = 0.30;
    public double CustoCapitalNeutroAnual { get; set; } = 0.295;
    public double EficienciaCapital { get; set; } = 0.035;
    public double DepreciacaoMensal { get; set; } = 0.005;

    // ---- Entrada/saída de firmas (margem extensiva: a população VIVA) ----
    public double UtilBreakeven { get; set; } = 0.90; // utilização de equilíbrio do lucro
    public double TaxaEntradaBase { get; set; } = 0.10;
    public double TaxaSaidaBase { get; set; } = 0.008;
    public int MaxEmpresas { get; set; } = 400;
    public int MinEmpresas { get; set; } = 12;

    public double TaxaInvestimento { get; private set; }
    public double InvestimentoReal { get; private set; }

    // Produtividade total dos fatores (>=1). A infraestrutura pública a eleva:
    // mesma capacidade instalada produz mais (Fase 3). Definida pela Simulacao.
    public double Produtividade { get; set; } = 1.0;

    public SetorProdutivo(double potencialInicial, int nFirmas = 60, int semente = 1)
    {
        _rng = new Random(semente);
        double cap = potencialInicial / nFirmas;
        for (int i = 0; i < nFirmas; i++) _empresas.Add(new Empresa(cap));
        ProdutoEfetivo = potencialInicial;
        _capacidadeAlvo = potencialInicial;
    }

    public ResultadoProducao Produzir(
        double consumoNominal,
        double inflacaoEsperadaMensal,
        double choqueOfertaMensal,
        double creditoAnual,
        double inflacaoEsperadaAnual,
        Politica pol)
    {
        double consumoReal = consumoNominal / NivelPrecos;
        // Capacidade instalada (soma das firmas) vezes a produtividade (infra).
        double potencial = ProdutoPotencial * Produtividade;
        double utilizacao = potencial > 0 ? ProdutoEfetivo / potencial : 1.0;

        // Investimento das firmas existentes (margem intensiva).
        double juroRealCredito = creditoAnual - inflacaoEsperadaAnual;
        double desvioJuro = juroRealCredito - CustoCapitalNeutroAnual;
        TaxaInvestimento = Math.Clamp(
            InvestAutonomo + Acelerador * (utilizacao - 1) - SensibJuro * desvioJuro
            - pol.ImpostoCorporativo * 0.5 + pol.SubsidioInvestimento,
            0.03, 0.40);
        InvestimentoReal = TaxaInvestimento * potencial;

        // Obras públicas: o investimento do governo é DEMANDA hoje (emprega) — e
        // vira infraestrutura/produtividade amanhã (tratado na Simulacao).
        double gastoPublicoReal = pol.InvestimentoPublico * ProdutoPotencial;

        // Demanda agregada real = consumo + investimento privado + obras públicas.
        double demandaReal = consumoReal + InvestimentoReal + gastoPublicoReal;
        ProdutoEfetivo = Math.Min(demandaReal, potencial * 1.03);
        double hiato = potencial > 0 ? (ProdutoEfetivo - potencial) / potencial : 0;

        // Capacidade-alvo agregada segue o caminho SUAVE do investimento (estável),
        // com piso na queda. É o que mantém o macro estável.
        double gNet = Math.Max(EficienciaCapital * TaxaInvestimento - DepreciacaoMensal, -0.003);
        _capacidadeAlvo *= 1 + gNet;
        foreach (var e in _empresas) e.Idade++;

        // Entrada/saída de firmas — a oferta VIVA (mexe na CONTAGEM).
        AtualizarPopulacaoEmpresas(utilizacao, juroRealCredito, pol);

        // Renormaliza: as firmas dividem a capacidade-alvo (quando uma fecha, as
        // sobreviventes absorvem seu mercado). Capacidade estável, contagem viva.
        double soma = _empresas.Sum(e => e.Capacidade);
        if (soma > 0) { double f = _capacidadeAlvo / soma; foreach (var e in _empresas) e.Capacidade *= f; }

        // Inflação: inércia + Phillips (hiato) + expectativa + choque.
        InflacaoMensal = InerciaInflacao * InflacaoMensal
                       + (1 - InerciaInflacao) * inflacaoEsperadaMensal
                       + RepassePhillips * hiato
                       + choqueOfertaMensal;
        NivelPrecos *= 1 + InflacaoMensal;

        return new ResultadoProducao(ProdutoEfetivo, hiato, InflacaoMensal, NivelPrecos,
                                     InvestimentoReal, TaxaInvestimento, _empresas.Count);
    }

    /// <summary>
    /// Índice de lucratividade decide entrada e falência: alto com utilização
    /// elevada (demanda não atendida) e crédito/impostos baixos.
    /// </summary>
    private void AtualizarPopulacaoEmpresas(double utilizacao, double juroRealCredito, Politica pol)
    {
        double lucro = (utilizacao - UtilBreakeven)
                     - 0.4 * (juroRealCredito - CustoCapitalNeutroAnual)
                     - 0.6 * pol.ImpostoCorporativo
                     + pol.SubsidioInvestimento;

        double media = _empresas.Count > 0 ? ProdutoPotencial / _empresas.Count : 1;

        // Entrada: lucro positivo atrai novas firmas (começam pequenas).
        if (lucro > 0 && _empresas.Count < MaxEmpresas)
        {
            int nEntrada = (int)Math.Round(Math.Clamp(lucro, 0, 0.5) * TaxaEntradaBase * _empresas.Count);
            for (int k = 0; k < nEntrada && _empresas.Count < MaxEmpresas; k++)
                _empresas.Add(new Empresa(media * 0.3));
        }

        // Saída/falência: sobe quando o lucro é negativo (crise).
        double probSaida = TaxaSaidaBase + Math.Max(0, -lucro) * 0.3;
        for (int i = _empresas.Count - 1; i >= 0 && _empresas.Count > MinEmpresas; i--)
            if (_rng.NextDouble() < probSaida) _empresas.RemoveAt(i);
    }

    /// <summary>Escala a capacidade (alvo e firmas) — ex.: acompanhar a população.</summary>
    public void EscalarCapacidade(double fator)
    {
        _capacidadeAlvo *= fator;
        foreach (var e in _empresas) e.Capacidade *= fator;
    }
}

public readonly record struct ResultadoProducao(
    double ProdutoEfetivo, double Hiato, double InflacaoMensal, double NivelPrecos,
    double InvestimentoReal, double TaxaInvestimento, int NumEmpresas);
