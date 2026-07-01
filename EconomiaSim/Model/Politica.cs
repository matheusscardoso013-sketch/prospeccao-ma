namespace EconomiaSim.Model;

/// <summary>
/// Conjunto de alavancas de política (incentivos/desincentivos) que o usuário
/// manipula para analisar causalidade. Mantidas constantes ao longo de um cenário
/// (bom para experimento ceteris paribus). Valores neutros = 0 reproduzem o
/// baseline sem intervenção.
/// </summary>
public class Politica
{
    // ---- Famílias ----
    public double IRProgressivo { get; set; }        // alíquota marginal do topo (classe A); cai p/ classes baixas
    public double TransferenciaMensal { get; set; }  // renda básica / Bolsa Família às classes baixas (nominal inicial)
    public double ImpostoConsumo { get; set; }       // IVA/ICMS sobre o consumo (regressivo)
    public double SalarioMinimo { get; set; }        // piso salarial (nominal inicial)

    // ---- Empresas ----
    public double ImpostoCorporativo { get; set; }   // tributa o lucro -> desincentiva investimento
    public double SubsidioInvestimento { get; set; } // desoneração -> incentiva investimento
    public double EncargosTrabalhistas { get; set; } // custo de contratar -> desincentiva emprego

    // ---- Governo (Fase 3) ----
    public double InvestimentoPublico { get; set; }  // % do PIB investido em infraestrutura

    /// <summary>Peso do IR por classe (A paga a alíquota cheia; E não paga). Progressivo.</summary>
    public static double PesoIR(ClasseRenda c) => c switch
    {
        ClasseRenda.A => 1.00, ClasseRenda.B => 0.70, ClasseRenda.C => 0.40,
        ClasseRenda.D => 0.15, ClasseRenda.E => 0.00, _ => 0
    };

    /// <summary>Peso da transferência por classe (vai para a base).</summary>
    public static double PesoTransferencia(ClasseRenda c) => c switch
    {
        ClasseRenda.D => 0.6, ClasseRenda.E => 1.0, _ => 0.0
    };

    public Politica Clonar() => (Politica)MemberwiseClone();
}
