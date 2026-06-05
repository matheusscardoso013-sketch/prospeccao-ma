namespace Prospeccao.Web.IA;

/// <summary>Resultado da qualificação de um lead pela IA.</summary>
public class ResultadoQualificacao
{
    /// <summary>Nota de sinergia 0–100.</summary>
    public int Score { get; set; }

    /// <summary>Racional textual curto.</summary>
    public string Racional { get; set; } = string.Empty;

    /// <summary>
    /// false quando a IA não retornou JSON válido. Nesse caso o job grava o lead
    /// como "não pontuado" preservando o motivo — nunca derruba o ciclo.
    /// </summary>
    public bool Sucesso { get; set; }

    public static ResultadoQualificacao Falha(string motivo) =>
        new() { Sucesso = false, Score = 0, Racional = motivo };
}
