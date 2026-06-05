using Npgsql;

namespace Prospeccao.Web.Data;

/// <summary>
/// Normaliza a connection string do Neon. O painel do Neon costuma entregar a URI
/// `postgresql://usuario:senha@host/banco?sslmode=require`, que o Npgsql NÃO aceita
/// direto — ele espera o formato `Host=...;Username=...`. Esta classe converte a URI
/// quando necessário, mantendo intacta a string que já vier no formato key=value.
/// </summary>
public static class NeonConnectionString
{
    public static string Normalizar(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var ehUri = raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                 || raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
        if (!ehUri)
            return raw; // já está no formato Npgsql (key=value)

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            SslMode = SslMode.Require // no Npgsql 8 já criptografa sem exigir validação de CA
        };
        return builder.ConnectionString;
    }
}
