using System.Globalization;
using System.Text;
using EconomiaSim.Llm;
using EconomiaSim.Model;

// ----------------------------------------------------------------------------
// EconomiaSim — protótipo de simulação socioeconômica baseada em agentes (ABM).
// Foco: efeito DISTRIBUTIVO das mudanças de política (Selic / inflação) entre
// classes de renda (A–E).
//
// Uso:
//   dotnet run                  -> cenário com choque inflacionário + super Selic
//   dotnet run -- padrao        -> cenário base (BC seguindo Regra de Taylor)
//   dotnet run -- llm           -> ao final, consulta agentes-LLM via Ollama
//   dotnet run -- experimento   -> laboratório causal: baseline vs. tratamentos
// ----------------------------------------------------------------------------

var ci = CultureInfo.GetCultureInfo("pt-BR");
bool usarLlm = args.Contains("llm");
bool padrao = args.Contains("padrao");

if (args.Contains("experimento"))
{
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine("== EconomiaSim — laboratório de causalidade (A/B ceteris paribus) ==");
    RodarExperimentos();
    return;
}

Cenario cenario = padrao ? Cenario.Padrao() : Cenario.ChoqueESuperSelic();

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("== EconomiaSim — simulação socioeconômica baseada em agentes ==\n");
Console.WriteLine($"Cenário ......: {(padrao ? "base (Regra de Taylor)" : "choque inflacionário + super Selic")}");
Console.WriteLine($"Famílias .....: {cenario.NumeroFamilias:N0}");
Console.WriteLine($"Horizonte ....: {cenario.Meses} meses\n");

var sim = new Simulacao(cenario);
var historico = sim.Rodar();

// --- Relatório resumido a cada 12 meses ---------------------------------------
Console.WriteLine($"{"Mês",4} {"Selic",7} {"IPCA",7} {"Desemp",7} {"Pop",6} {"Firmas",7} {"Gini",6}  {"Desemp E",9}");
Console.WriteLine(new string('-', 70));
foreach (var m in historico.Where(m => m.Mes % 12 == 0))
{
    Console.WriteLine(
        $"{m.Mes,4} " +
        $"{m.SelicAnual,7:P1} " +
        $"{m.InflacaoAnual,7:P1} " +
        $"{m.DesempregoTotal,7:P1} " +
        $"{m.Populacao,6} " +
        $"{m.NumEmpresas,7} " +
        $"{m.Gini,6:F3}  " +
        $"{m.PorClasse[ClasseRenda.E].Desemprego,9:P1}");
}

// --- Leitura distributiva: quem ganhou e quem perdeu --------------------------
var ini = historico.First();
var fim = historico.Last();
Console.WriteLine("\n== Efeito distributivo (patrimônio real médio: início -> fim) ==");
foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
{
    double p0 = ini.PorClasse[c].PatrimonioMedioReal;
    double p1 = fim.PorClasse[c].PatrimonioMedioReal;
    double varp = p0 != 0 ? (p1 - p0) / Math.Abs(p0) : 0;
    Console.WriteLine($"  Classe {c}: {p0,12:N0} -> {p1,12:N0}  ({varp,7:P1})");
}
Console.WriteLine($"\nGini: {ini.Gini:F3} -> {fim.Gini:F3} " +
                  $"({(fim.Gini > ini.Gini ? "desigualdade SUBIU" : "desigualdade CAIU")})");

// --- Exporta CSV para análise/gráficos ----------------------------------------
string csv = ExportarCsv(historico);
string caminho = Path.Combine(AppContext.BaseDirectory, "resultado.csv");
File.WriteAllText(caminho, csv);
Console.WriteLine($"\nCSV exportado em: {caminho}");

// --- Camada híbrida opcional (agentes-LLM via Ollama) -------------------------
if (usarLlm)
{
    Console.WriteLine("\n== Reação qualitativa das classes (Ollama) ==");
    var agente = new OllamaAgente();
    if (await agente.DisponivelAsync())
    {
        foreach (ClasseRenda c in new[] { ClasseRenda.A, ClasseRenda.C, ClasseRenda.E })
        {
            string r = await agente.ReacaoDaClasseAsync(c, fim);
            Console.WriteLine($"\n[Classe {c}] {r}");
        }
    }
    else
    {
        Console.WriteLine("Ollama não está acessível em http://localhost:11434 — pulei esta etapa.");
    }
}

static string ExportarCsv(List<RegistroMes> h)
{
    var sb = new StringBuilder();
    sb.Append("mes,selic,ipca,desemprego,pib,investimento,populacao,empresas,infraestrutura,gini");
    foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
        sb.Append($",desemp_{c},consumo_{c},patrimonio_{c}");
    sb.AppendLine();

    var inv = CultureInfo.InvariantCulture;
    foreach (var m in h)
    {
        sb.Append($"{m.Mes},{m.SelicAnual.ToString(inv)},{m.InflacaoAnual.ToString(inv)}," +
                  $"{m.DesempregoTotal.ToString(inv)},{m.PibReal.ToString(inv)}," +
                  $"{m.TaxaInvestimento.ToString(inv)},{m.Populacao},{m.NumEmpresas}," +
                  $"{m.Infraestrutura.ToString(inv)},{m.Gini.ToString(inv)}");
        foreach (ClasseRenda c in Enum.GetValues<ClasseRenda>())
        {
            var mc = m.PorClasse[c];
            sb.Append($",{mc.Desemprego.ToString(inv)},{mc.ConsumoMedioReal.ToString(inv)},{mc.PatrimonioMedioReal.ToString(inv)}");
        }
        sb.AppendLine();
    }
    return sb.ToString();
}

static void RodarExperimentos()
{
    var t1 = Cenario.Padrao(); t1.Politica.TransferenciaMensal = 600;
    Experimento.Comparar("Renda básica de R$600 à base (classes D/E)", Cenario.Padrao(), t1);

    var t2 = Cenario.Padrao(); t2.Politica.ImpostoConsumo = 0.20;
    Experimento.Comparar("Imposto sobre consumo de 20%", Cenario.Padrao(), t2);

    var t3 = Cenario.Padrao(); t3.Politica.SubsidioInvestimento = 0.08;
    Experimento.Comparar("Desoneração/subsídio ao investimento", Cenario.Padrao(), t3);

    var t4 = Cenario.Padrao(); t4.Politica.SalarioMinimo = 1500;
    Experimento.Comparar("Salário mínimo alto (R$1.500)", Cenario.Padrao(), t4);

    var t5 = Cenario.Padrao(); t5.Politica.IRProgressivo = 0.30; t5.Politica.TransferenciaMensal = 600;
    Experimento.Comparar("IR progressivo (30% topo) + transferência", Cenario.Padrao(), t5);

    var t6 = Cenario.Padrao(); t6.Politica.InvestimentoPublico = 0.05;
    Experimento.Comparar("Investimento público em infraestrutura (5% do PIB)", Cenario.Padrao(), t6);
}
