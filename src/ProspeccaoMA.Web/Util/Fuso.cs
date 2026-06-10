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
}
