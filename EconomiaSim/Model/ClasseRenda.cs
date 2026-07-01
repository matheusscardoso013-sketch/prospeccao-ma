namespace EconomiaSim.Model;

/// <summary>
/// Classes de renda no estilo da estratificação brasileira (A a E).
/// Usadas para observar o efeito DISTRIBUTIVO das mudanças de política.
/// </summary>
public enum ClasseRenda
{
    A, // topo da pirâmide — alta poupança, credora
    B,
    C, // classe média
    D,
    E  // base — endividada, propensão a consumir ~100% da renda
}

public static class ClasseRendaInfo
{
    /// <summary>Parcela da população em cada classe (aprox. realidade brasileira).</summary>
    public static double Parcela(ClasseRenda c) => c switch
    {
        ClasseRenda.A => 0.03,
        ClasseRenda.B => 0.12,
        ClasseRenda.C => 0.30,
        ClasseRenda.D => 0.35,
        ClasseRenda.E => 0.20,
        _ => 0.0
    };

    /// <summary>Renda mensal de referência (unidades arbitrárias, calibráveis).</summary>
    public static double RendaBase(ClasseRenda c) => c switch
    {
        ClasseRenda.A => 20000,
        ClasseRenda.B => 8000,
        ClasseRenda.C => 3500,
        ClasseRenda.D => 1800,
        ClasseRenda.E => 900,
        _ => 0
    };

    /// <summary>
    /// Propensão marginal a consumir. Mais pobre = consome quase tudo.
    /// Mais rico = poupa mais (sensível à taxa de juros real).
    /// </summary>
    public static double PropensaoConsumo(ClasseRenda c) => c switch
    {
        ClasseRenda.A => 0.55,
        ClasseRenda.B => 0.70,
        ClasseRenda.C => 0.82,
        ClasseRenda.D => 0.92,
        ClasseRenda.E => 0.98,
        _ => 0.9
    };

    /// <summary>
    /// Patrimônio inicial como múltiplo da renda mensal.
    /// Negativo = endividado. Ricos são credores; pobres, devedores.
    /// </summary>
    public static double PatrimonioInicialEmMeses(ClasseRenda c) => c switch
    {
        ClasseRenda.A => 60,    // grande poupança
        ClasseRenda.B => 18,
        ClasseRenda.C => 3,
        ClasseRenda.D => -2,    // endividada
        ClasseRenda.E => -4,    // mais endividada (rotativo, crediário)
        _ => 0
    };
}
