namespace EconomiaSim.Model;

/// <summary>
/// Motor da simulação. Roda o loop mensal:
/// BC define Selic -> bancos repassam ao crédito -> famílias consomem/poupam ->
/// setor produz, gera inflação e emprego -> apura métricas distributivas.
/// </summary>
public class Simulacao
{
    private readonly Cenario _cfg;
    private readonly Random _rng;
    private readonly List<Familia> _familias = new();
    private readonly BancoCentral _bc;
    private readonly SetorProdutivo _setor;

    // Estado demográfico (a população deixa de ser fixa).
    private int _proxId;
    private readonly int _popMax;
    private int _popAnterior;
    private double _infra = 1.0; // estoque de infraestrutura pública (Fase 3)

    // Infraestrutura: eficiência da construção e depreciação (mensais).
    private const double EficInfra = 0.03;
    private const double DeprecInfra = 0.005;

    public IReadOnlyList<Familia> Familias => _familias;

    public Simulacao(Cenario cfg)
    {
        _cfg = cfg;
        _rng = new Random(cfg.Semente);
        double inflMensalInicial = Anual2Mensal(cfg.InflacaoInicialAnual);

        // Cria a população respeitando a parcela de cada classe.
        int id = 0;
        foreach (ClasseRenda classe in Enum.GetValues<ClasseRenda>())
        {
            int qtd = (int)Math.Round(cfg.NumeroFamilias * ClasseRendaInfo.Parcela(classe));
            for (int i = 0; i < qtd; i++)
            {
                var f = new Familia(id++, classe, inflMensalInicial);
                // Ruído idiossincrático na renda (±20%) para heterogeneidade.
                f.Salario *= 0.8 + _rng.NextDouble() * 0.4;
                _familias.Add(f);
            }
        }

        _bc = new BancoCentral(cfg.MetaInflacaoAnual, cfg.JuroNeutralAnual, cfg.SelicInicialAnual);

        // Calibra o produto potencial pela DEMANDA inicial efetiva, para começar
        // próximo ao equilíbrio (hiato ~ 0) em vez de chutar um fator.
        double poupIni = Anual2Mensal(Math.Max(0, cfg.SelicInicialAnual - 0.01));
        double credIni = Anual2Mensal(cfg.SelicInicialAnual + cfg.SpreadBancarioAnual);
        double demandaIni = 0;
        var politicaNeutra = new Politica();
        foreach (var f in _familias)
        {
            f.DecidirConsumoEPoupanca(poupIni, credIni, politicaNeutra, 0);
            demandaIni += f.UltimoConsumo;
        }
        // Restaura o patrimônio inicial (a passada acima era só para calibrar).
        foreach (var f in _familias)
            f.Patrimonio = ClasseRendaInfo.PatrimonioInicialEmMeses(f.Classe) * f.Salario;

        // Potencial calibrado para que CONSUMO + INVESTIMENTO inicial ~ potencial
        // (hiato ~ 0). Como o investimento é ~InvestAutonomo do potencial:
        //   potencial = consumo / (1 - InvestAutonomo).
        _setor = new SetorProdutivo(demandaIni / (1 - 0.20), 60, cfg.Semente);
        _setor.CustoCapitalNeutroAnual = cfg.JuroNeutralAnual + cfg.SpreadBancarioAnual;

        _proxId = _familias.Count;
        _popMax = (int)(_familias.Count * 2.5);
        _popAnterior = _familias.Count;
    }

    public List<RegistroMes> Rodar(Action<int>? aoIniciarMes = null)
    {
        var historico = new List<RegistroMes>();
        var pol = _cfg.Politica;
        double inflacaoAnualObs = _cfg.InflacaoInicialAnual;
        double metaMensal = Anual2Mensal(_cfg.MetaInflacaoAnual);
        double inflMensalAnterior = Anual2Mensal(_cfg.InflacaoInicialAnual);
        double salarioMinimoAtual = pol.SalarioMinimo;
        _infra = 1.0; // estoque de infraestrutura (índice; 1 = baseline)

        for (int mes = 0; mes < _cfg.Meses; mes++)
        {
            aoIniciarMes?.Invoke(mes);
            var choque = _cfg.Choques?.Invoke(mes) ?? new ChoqueMes(0, null);

            // 0) Reajuste salarial: repassa parte da inflação passada + ganho real
            //    de produtividade (tendência). O piso (salário mínimo) é reajustado
            //    junto, para não ser corroído pela inflação ao longo dos anos.
            double reajuste = 1 + _cfg.IndexacaoSalarial * inflMensalAnterior
                                + _setor.CrescimentoTendencialMensal;
            if (mes > 0)
            {
                foreach (var f in _familias) f.Salario *= reajuste;
                salarioMinimoAtual *= reajuste;
            }

            // 1) Banco Central define a Selic (ou usa valor forçado pelo cenário).
            double hiato = (_setor.ProdutoEfetivo - _setor.ProdutoPotencial) / _setor.ProdutoPotencial;
            double selicAnual;
            if (choque.SelicForcadaAnual is double forcada)
            {
                _bc.ForcarSelic(forcada);
                selicAnual = forcada;
            }
            else
            {
                selicAnual = _bc.DefinirSelic(inflacaoAnualObs, hiato);
            }

            // 2) Bancos repassam: poupança ~ Selic; crédito = Selic + spread.
            double poupMensal = Anual2Mensal(Math.Max(0, selicAnual - 0.01));
            double creditoMensal = Anual2Mensal(selicAnual + _cfg.SpreadBancarioAnual);

            // 3) Famílias decidem consumo e poupança (com IR, transferência, piso).
            double demandaNominal = 0;
            foreach (var f in _familias)
            {
                f.DecidirConsumoEPoupanca(poupMensal, creditoMensal, pol, salarioMinimoAtual);
                demandaNominal += f.UltimoConsumo;
            }

            // 4) Setor produtivo: investimento + consumo -> produção, inflação, preços.
            //    A infraestrutura pública eleva a produtividade (mais oferta) — efeito
            //    moderado, para as obras (demanda) gerarem emprego no líquido.
            _setor.Produtividade = 1 + 0.3 * (_infra - 1);
            double inflEsperadaMensal = _familias.Average(f => f.ExpectativaInflacao);
            double creditoAnual = selicAnual + _cfg.SpreadBancarioAnual;
            double inflEsperadaAnual = Mensal2Anual(inflEsperadaMensal);
            var prod = _setor.Produzir(demandaNominal, inflEsperadaMensal,
                                       choque.ChoqueOfertaMensal, creditoAnual, inflEsperadaAnual, pol);

            // 5) Mercado de trabalho: o emprego segue o hiato (produção vs.
            //    capacidade das firmas). Quando firmas fecham, o potencial cai e o
            //    hiato/emprego sentem. Salário mínimo binding e encargos DESINCENTIVAM.
            double fracaoBinding = _familias.Count(f => f.Salario < salarioMinimoAtual) / (double)_familias.Count;
            double desincentivoEmprego = fracaoBinding * 0.25 + pol.EncargosTrabalhistas * 0.5;
            double taxaDesempregoAlvo = Math.Clamp(0.07 - prod.Hiato * 1.2 + desincentivoEmprego, 0.02, 0.45);
            AjustarEmprego(taxaDesempregoAlvo);

            // 6) Expectativas (adaptativas + âncora na meta) para o próximo mês.
            foreach (var f in _familias)
                f.AtualizarExpectativa(prod.InflacaoMensal, metaMensal, _cfg.CredibilidadeBC);

            inflMensalAnterior = prod.InflacaoMensal;
            // Inflação anualizada observada (composição dos 12 últimos meses aprox.).
            inflacaoAnualObs = Mensal2Anual(prod.InflacaoMensal);

            // 7) Demografia: nascimentos, mortes e mobilidade social (a sociedade VIVE).
            //    Boa infraestrutura de saúde reduz a mortalidade.
            double fatorSaude = Math.Clamp(1 - 0.5 * (_infra - 1), 0.5, 1.3);
            AtualizarDemografia(prod.NivelPrecos, inflacaoAnualObs, fatorSaude);

            // 8) Mais gente = mais trabalho disponível, mas famílias novas são
            //    jovens/pobres e contribuem ~metade da média para a capacidade.
            //    (Escalar pela população cheia geraria excesso de oferta/deflação.)
            double ratioPop = _popAnterior > 0 ? _familias.Count / (double)_popAnterior : 1.0;
            _setor.EscalarCapacidade(1 + 0.5 * (ratioPop - 1));
            _popAnterior = _familias.Count;

            // 9) Governo: investimento público constrói infraestrutura (que deprecia).
            _infra += EficInfra * pol.InvestimentoPublico - DeprecInfra * (_infra - 1);

            historico.Add(Apurar(mes, selicAnual, inflacaoAnualObs, prod, pol));
        }

        return historico;
    }

    private void AjustarEmprego(double taxaDesempregoAlvo)
    {
        int total = _familias.Count;
        int desejarEmpregados = (int)Math.Round(total * (1 - taxaDesempregoAlvo));

        // Ordena por "proteção no emprego": classes mais altas mantêm o emprego primeiro.
        var ordenadas = _familias.OrderBy(f => (int)f.Classe)
                                 .ThenBy(_ => _rng.Next())
                                 .ToList();
        for (int i = 0; i < ordenadas.Count; i++)
            ordenadas[i].Empregado = i < desejarEmpregados;
    }

    /// <summary>Nascimentos, mortes e mobilidade social — a sociedade viva.</summary>
    private void AtualizarDemografia(double nivelPrecos, double inflacaoAnual, double fatorSaude)
    {
        var morrer = new List<Familia>();
        var nascer = new List<Familia>();
        foreach (var f in _familias)
        {
            f.MesesDesempregado = f.Empregado ? 0 : f.MesesDesempregado + 1;
            double patRel = f.Patrimonio / nivelPrecos;
            // Prosperidade = renda real / renda de referência da classe (scale-free).
            double rendaReal = f.UltimaRendaDisponivel / nivelPrecos;
            double prosperidade = rendaReal / ClasseRendaInfo.RendaBase(f.Classe);

            if (_rng.NextDouble() < Demografia.ProbMorte(f, patRel, prosperidade, fatorSaude)) { morrer.Add(f); continue; }

            if (_familias.Count + nascer.Count < _popMax
                && _rng.NextDouble() < Demografia.ProbNascimento(f, inflacaoAnual, prosperidade))
                nascer.Add(f);

            int dir = Demografia.DirecaoMobilidade(f, patRel);
            if (dir != 0 && _rng.NextDouble() < 0.04)
                MudarClasse(f, (ClasseRenda)Math.Clamp((int)f.Classe + dir, 0, 4));
        }
        foreach (var m in morrer) _familias.Remove(m);
        foreach (var pai in nascer) _familias.Add(CriarFilho(pai));
    }

    private Familia CriarFilho(Familia pai)
    {
        // Novo domicílio na mesma classe, com renda próxima à do "pai" e pouco
        // patrimônio acumulado (família jovem).
        var f = new Familia(_proxId++, pai.Classe, pai.ExpectativaInflacao)
        {
            Salario = pai.Salario * (0.7 + _rng.NextDouble() * 0.4)
        };
        f.Patrimonio = ClasseRendaInfo.PatrimonioInicialEmMeses(pai.Classe) * f.Salario * 0.2;
        return f;
    }

    private static void MudarClasse(Familia f, ClasseRenda nova)
    {
        // Mobilidade muda o COMPORTAMENTO (propensão a consumir, bucket de relatório),
        // não injeta renda artificial — a renda evolui pelo salário e pela poupança.
        // Evita um laço inflacionário espúrio (poupador rico "sobe" e dobra a renda).
        f.Classe = nova;
        f.MesesDesempregado = 0;
    }

    private RegistroMes Apurar(int mes, double selicAnual, double inflacaoAnual, ResultadoProducao prod, Politica pol)
    {
        // UltimoConsumo já são os BENS recebidos (líquido do imposto sobre consumo).
        var porClasse = new Dictionary<ClasseRenda, MetricaClasse>();
        foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
        {
            var grupo = _familias.Where(f => f.Classe == c).ToList();
            porClasse[c] = grupo.Count == 0
                ? new MetricaClasse(0, 0, 0, 0)
                : new MetricaClasse(
                    Desemprego: 1 - grupo.Average(f => f.Empregado ? 1.0 : 0.0),
                    ConsumoMedioReal: grupo.Average(f => f.UltimoConsumo) / prod.NivelPrecos,
                    PatrimonioMedioReal: grupo.Average(f => f.Patrimonio) / prod.NivelPrecos,
                    Quantidade: grupo.Count);
        }

        return new RegistroMes(
            Mes: mes,
            SelicAnual: selicAnual,
            InflacaoAnual: inflacaoAnual,
            DesempregoTotal: _familias.Count == 0 ? 0 : 1 - _familias.Average(f => f.Empregado ? 1.0 : 0.0),
            PibReal: prod.ProdutoEfetivo,
            TaxaInvestimento: prod.TaxaInvestimento,
            Populacao: _familias.Count,
            NumEmpresas: prod.NumEmpresas,
            Infraestrutura: _infra,
            Gini: Gini(_familias.Select(f => Math.Max(0, f.UltimaRendaDisponivel))),
            PorClasse: porClasse);
    }

    /// <summary>Coeficiente de Gini (0 = igualdade, 1 = desigualdade máxima).</summary>
    public static double Gini(IEnumerable<double> valores)
    {
        var v = valores.OrderBy(x => x).ToArray();
        int n = v.Length;
        if (n == 0) return 0;
        double soma = v.Sum();
        if (soma == 0) return 0;
        double acum = 0;
        for (int i = 0; i < n; i++) acum += (i + 1) * v[i];
        return (2.0 * acum) / (n * soma) - (n + 1.0) / n;
    }

    private static double Anual2Mensal(double anual) => Math.Pow(1 + anual, 1.0 / 12) - 1;
    private static double Mensal2Anual(double mensal) => Math.Pow(1 + mensal, 12) - 1;
}

public readonly record struct MetricaClasse(
    double Desemprego, double ConsumoMedioReal, double PatrimonioMedioReal, int Quantidade);

public readonly record struct RegistroMes(
    int Mes,
    double SelicAnual,
    double InflacaoAnual,
    double DesempregoTotal,
    double PibReal,
    double TaxaInvestimento,
    int Populacao,
    int NumEmpresas,
    double Infraestrutura,
    double Gini,
    IReadOnlyDictionary<ClasseRenda, MetricaClasse> PorClasse);
