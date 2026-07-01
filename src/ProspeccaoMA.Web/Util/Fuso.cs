namespace ProspeccaoMA.Web.Util;

/// <summary>
/// Conversão UTC → horário de Brasília para exibição. O container do Render roda em UTC,
/// então DateTime.ToLocalTime() mostraria hora errada (+3h) nas telas.
/// </summary>
public static class Fuso
{
    private static readonly TimeZoneInfo Brasilia = Resolver();

    private static TimeZoneInfo Resolver()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { /* tenta o próximo */ }
        }
        return TimeZoneInfo.Utc;
    }

    public static DateTime Brasil(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(
            utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            Brasilia);

    /// <summary>Agora, no horário de Brasília.</summary>
    public static DateTime Agora => Brasil(DateTime.UtcNow);

    /// <summary>Instante UTC correspondente à meia-noite de hoje em Brasília
    /// (para filtrar registros "de hoje" cujo carimbo é gravado em UTC).</summary>
    public static DateTime InicioHojeUtc()
    {
        var meiaNoiteBr = DateTime.SpecifyKind(Agora.Date, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(meiaNoiteBr, Brasilia);
    }
}
