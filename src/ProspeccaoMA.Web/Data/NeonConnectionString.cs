namespace ProspeccaoMA.Web.Data;

/// <summary>
/// O painel do Neon fornece a connection string no formato URI
/// (postgresql://user:senha@host/db?sslmode=require). O Npgsql espera o formato
/// key=value. Este helper aceita os dois e normaliza para key=value.
/// A connection string NUNCA fica no código — vem de env var / user-secrets.
/// </summary>
public static class NeonConnectionString
{
    public static string Normalizar(string? bruta)
    {
        if (string.IsNullOrWhiteSpace(bruta))
            return string.Empty;

        bruta = bruta.Trim();

        var ehUri = bruta.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                 || bruta.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        if (!ehUri)
            return bruta; // já está em key=value

        var uri = new Uri(bruta);
        var userInfo = uri.UserInfo.Split(':', 2);
        var usuario = Uri.UnescapeDataString(userInfo[0]);
        var senha = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        var banco = uri.AbsolutePath.Trim('/');
        var porta = uri.Port > 0 ? uri.Port : 5432;

        var b = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = porta,
            Username = usuario,
            Password = senha,
            Database = banco,
            SslMode = Npgsql.SslMode.Require
        };

        return b.ConnectionString;
    }
}
