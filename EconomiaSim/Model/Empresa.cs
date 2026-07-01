namespace EconomiaSim.Model;

/// <summary>
/// Empresa individual. O setor produtivo é uma POPULAÇÃO destas (Fase 2): firmas
/// nascem (entrada) quando há lucro e fecham (falência) na crise. A soma das
/// capacidades é o produto potencial; os empregos vêm da produção das firmas.
/// </summary>
public class Empresa
{
    public double Capacidade { get; set; } // produto real que consegue ofertar
    public int Idade { get; set; }          // meses desde a fundação

    public Empresa(double capacidade) => Capacidade = capacidade;
}
