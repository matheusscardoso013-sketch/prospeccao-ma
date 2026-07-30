using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Util;

/// <summary>
/// Um par com <c>Score == 0</c> NÃO é um par ruim: é um par que a IA nunca conseguiu
/// avaliar (cota do free tier estourada, erro de rede). A rubrica começa em 1, então
/// zero é reservado para "sem veredito".
///
/// A distinção importa porque esses registros pareciam oportunidades: em 30/07 eram
/// 1.133 de 1.374 linhas "Novo" (82,5%), inflando os KPIs do painel e o funil. A lista
/// da Mesa já os escondia por filtrar score >= 50 — os contadores é que não.
///
/// Todo lugar que conta pares para o time deve usar <see cref="Avaliadas"/>; quem quiser
/// mostrar a fila pendente usa <see cref="NaoAvaliadas"/>, de forma explícita e honesta.
/// </summary>
public static class Sinergias
{
    public static IQueryable<SinergiaComprador> Avaliadas(this IQueryable<SinergiaComprador> q)
        => q.Where(s => s.Score > 0);

    public static IQueryable<SinergiaComprador> NaoAvaliadas(this IQueryable<SinergiaComprador> q)
        => q.Where(s => s.Score == 0);
}
