using System.Collections.Concurrent;

namespace ProspeccaoMA.Web.Util;

/// <summary>
/// Catálogo oficial de CNAEs (arquivo público da Receita, Dados/cnaes.csv) para traduzir
/// o código em descrição legível — nas telas e nos prompts da IA. Carregado uma vez.
/// </summary>
public static class CnaeCatalogo
{
    private static readonly Lazy<Dictionary<string, string>> _mapa = new(Carregar);
    private static readonly ConcurrentDictionary<string, string> _cacheRotulo = new();

    private static Dictionary<string, string> Carregar()
    {
        var dict = new Dictionary<string, string>();
        try
        {
            var caminho = Path.Combine(AppContext.BaseDirectory, "Dados", "cnaes.csv");
            if (!File.Exists(caminho)) return dict;
            foreach (var linha in File.ReadLines(caminho))
            {
                // Formato oficial: "0111301";"Cultivo de arroz"
                var partes = linha.Split(';');
                if (partes.Length < 2) continue;
                var codigo = partes[0].Trim().Trim('"');
                var desc = partes[1].Trim().Trim('"');
                if (codigo.Length > 0 && desc.Length > 0) dict[codigo] = desc;
            }
        }
        catch { /* sem catálogo, os códigos aparecem crus — nunca derruba o app */ }
        return dict;
    }

    /// <summary>Descrição oficial do CNAE (subclasse exata), ou null se desconhecido.</summary>
    public static string? Descricao(string? cnae)
    {
        var d = SoDigitos(cnae);
        if (d.Length == 0) return null;
        return _mapa.Value.TryGetValue(d, out var desc) ? desc : null;
    }

    /// <summary>Rótulo amigável para telas: descrição (truncada) ou o código cru como veio.</summary>
    public static string Rotulo(string? cnae, int max = 46)
    {
        if (string.IsNullOrWhiteSpace(cnae)) return "";
        return _cacheRotulo.GetOrAdd($"{cnae}|{max}", _ =>
        {
            var desc = Descricao(cnae);
            if (desc is null) return $"CNAE {cnae}";
            return desc.Length > max ? desc[..(max - 1)].TrimEnd() + "…" : desc;
        });
    }

    /// <summary>Linha completa para prompts da IA: "6203100 — Desenvolvimento e licenciamento…".</summary>
    public static string ParaPrompt(string? cnae)
    {
        if (string.IsNullOrWhiteSpace(cnae)) return "";
        var desc = Descricao(cnae);
        return desc is null ? cnae! : $"{cnae} — {desc}";
    }

    private static string SoDigitos(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
}
