using System.Globalization;

namespace EconomiaSim.Model;

/// <summary>
/// Máquina de experimento controlado (ceteris paribus). Roda um cenário BASELINE
/// e um TRATAMENTO (que muda uma alavanca) e imprime o diff causal: o que mudou
/// nos agregados e em cada classe de renda, e por qual canal.
/// </summary>
public static class Experimento
{
    public static void Comparar(string nome, Cenario baseline, Cenario tratamento, int janela = 24)
    {
        var b = Resumir(new Simulacao(baseline).Rodar(), janela);
        var t = Resumir(new Simulacao(tratamento).Rodar(), janela);

        Console.WriteLine($"\n=== EXPERIMENTO: {nome} ===");
        Console.WriteLine($"(médias dos últimos {janela} meses; baseline -> tratamento)\n");

        Console.WriteLine($"  {"Agregado",-14} {"Baseline",10} {"Tratamento",12} {"Δ",10}");
        LinhaPP("IPCA (a.a.)", b.Ipca, t.Ipca);
        LinhaPP("Selic (a.a.)", b.Selic, t.Selic);
        LinhaPP("Desemprego", b.Desemprego, t.Desemprego);
        LinhaPP("Investimento", b.Invest, t.Invest);
        Console.WriteLine($"  {"População",-14} {b.Pop,10:N0} {t.Pop,12:N0} {(t.Pop - b.Pop),+10:N0}");
        Console.WriteLine($"  {"Empresas",-14} {b.Firmas,10:N0} {t.Firmas,12:N0} {(t.Firmas - b.Firmas),+10:N0}");
        Console.WriteLine($"  {"Infraestrut.",-14} {b.Infra,10:F2} {t.Infra,12:F2} {(t.Infra - b.Infra),+10:F2}");
        Console.WriteLine($"  {"Gini",-14} {b.Gini,10:F3} {t.Gini,12:F3} {(t.Gini - b.Gini),+10:F3}");

        Console.WriteLine($"\n  {"Consumo real médio por classe",-30} {"Δ %",10}");
        foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
        {
            double d = b.Consumo[c] != 0 ? (t.Consumo[c] - b.Consumo[c]) / b.Consumo[c] : 0;
            Console.WriteLine($"    Classe {c}: {b.Consumo[c],12:N0} -> {t.Consumo[c],12:N0} {d,9:P1}");
        }

        Console.WriteLine($"\n  {"Desemprego por classe",-30}");
        foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
            Console.WriteLine($"    Classe {c}: {b.Desemp[c],8:P1} -> {t.Desemp[c],8:P1} {(t.Desemp[c] - b.Desemp[c]),+9:P1}");
    }

    private static void LinhaPP(string nome, double bv, double tv)
        => Console.WriteLine($"  {nome,-14} {bv,10:P1} {tv,12:P1} {(tv - bv) * 100,+9:F1}pp");

    private static Resumo Resumir(List<RegistroMes> h, int janela)
    {
        var ult = h.TakeLast(janela).ToList();
        var r = new Resumo
        {
            Ipca = ult.Average(m => m.InflacaoAnual),
            Selic = ult.Average(m => m.SelicAnual),
            Desemprego = ult.Average(m => m.DesempregoTotal),
            Invest = ult.Average(m => m.TaxaInvestimento),
            Pop = ult.Average(m => (double)m.Populacao),
            Firmas = ult.Average(m => (double)m.NumEmpresas),
            Infra = ult.Average(m => m.Infraestrutura),
            Gini = ult.Average(m => m.Gini)
        };
        foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
        {
            r.Consumo[c] = ult.Average(m => m.PorClasse[c].ConsumoMedioReal);
            r.Desemp[c] = ult.Average(m => m.PorClasse[c].Desemprego);
        }
        return r;
    }

    private class Resumo
    {
        public double Ipca, Selic, Desemprego, Invest, Pop, Firmas, Infra, Gini;
        public Dictionary<ClasseRenda, double> Consumo = new();
        public Dictionary<ClasseRenda, double> Desemp = new();
    }
}
