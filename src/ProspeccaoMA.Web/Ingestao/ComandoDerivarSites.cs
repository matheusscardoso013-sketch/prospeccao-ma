using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Deriva o SITE das empresas da Receita a partir do domínio do e-mail corporativo.
///
/// Por que importa: a busca semântica por tese ranqueia usando o texto que descreve o alvo.
/// Um alvo curado tem "SaaS de PDV/POS para supermercados"; um da Receita tem só razão
/// social e "Desenvolvimento de programas de computador", que descreve milhares de empresas
/// de forma idêntica. Em 13/08, cruzando a tese da Starian contra 1.783 alvos vetorizados,
/// NENHUMA empresa da Receita entrou no top 25 — não por falta de aderência, mas porque o
/// dado público não diz o que o software faz.
///
/// O e-mail resolve isso de graça: "contato@sienge.com.br" entrega o domínio, o domínio
/// entrega o site, e o site alimenta o PerfilSite que a plataforma já sabe extrair. Sem
/// inventar nada — a fonte continua sendo o site oficial da empresa.
///
/// Provedores gratuitos são descartados: um e-mail @gmail.com não diz nada sobre a empresa.
/// Uso: dotnet run --project src/ProspeccaoMA.Web -- derivar-sites [--gravar]
/// </summary>
public static class ComandoDerivarSites
{
    private static readonly string[] Gratuitos =
    {
        "gmail.", "hotmail.", "outlook.", "yahoo.", "live.", "icloud.", "bol.com",
        "uol.com", "terra.com", "ig.com.br", "globo.com", "msn.com", "aol.com",
        "protonmail.", "zipmail.", "r7.com", "oi.com.br", "me.com"
    };

    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var leads = await db.Leads
            .Where(l => l.Origem == Lead.OrigemReceita && l.Contato != null
                     && (l.Site == null || l.Site == ""))
            .ToListAsync();

        int comSite = 0, gratuito = 0, semEmail = 0;
        var exemplos = new List<string>();

        var descasados = new List<string>();
        foreach (var l in leads)
        {
            var dominio = DominioDoContato(l.Contato);
            if (dominio is null)
            {
                if (l.Contato!.Contains("E-mail:")) gratuito++; else semEmail++;
                continue;
            }

            // É PRAXE no Brasil registrar o e-mail do CONTADOR na Receita. Sem esta
            // checagem, "GRUPO MASTELLINI" viraria "escritoriofutura.com.br" e a plataforma
            // descreveria o escritório de contabilidade como se fosse o alvo — pior que não
            // ter dado, porque estaria confiantemente errada. Só aceitamos o domínio quando
            // ele se parece com o nome da empresa.
            if (!Combina(l.RazaoSocial, dominio))
            {
                if (descasados.Count < 8) descasados.Add($"{l.RazaoSocial}  ~/~  {dominio}");
                continue;
            }

            if (gravar) l.Site = dominio;
            comSite++;
            if (exemplos.Count < 12) exemplos.Add($"{l.RazaoSocial}  ->  {dominio}");
        }

        Console.WriteLine($"Leads da Receita sem site: {leads.Count}");
        Console.WriteLine($"  ganham site (domínio bate com o nome): {comSite}");
        Console.WriteLine($"  domínio de terceiro (contador etc.) — descartado: {descasados.Count switch { 0 => 0, _ => leads.Count - comSite - gratuito - semEmail }}");
        Console.WriteLine($"  e-mail de provedor gratuito (descartado): {gratuito}");
        Console.WriteLine($"  sem e-mail no cadastro: {semEmail}\n");

        if (descasados.Count > 0)
        {
            Console.WriteLine("Amostra do que foi DESCARTADO por não bater:");
            foreach (var d in descasados) Console.WriteLine($"  {d}");
            Console.WriteLine();
        }

        Console.WriteLine("Amostra:");
        foreach (var e in exemplos) Console.WriteLine($"  {e}");

        if (!gravar) { Console.WriteLine("\nDRY-RUN — nada gravado. Use --gravar."); return; }

        await db.SaveChangesAsync();
        Console.WriteLine($"\n{comSite} site(s) preenchido(s). O dreno diário passa a extrair o perfil deles.");
    }

    /// <summary>
    /// O domínio pertence à própria empresa? Compara o rótulo principal do domínio com as
    /// palavras da razão social, ignorando termos societários e genéricos que apareceriam em
    /// qualquer nome. Aceita quando uma palavra relevante do nome está contida no domínio
    /// (METASIX TECNOLOGIA → metasix.com.br) ou quando o domínio, sem separadores, aparece no
    /// nome (3am-it.com.br → 3AM IT SERVICES).
    /// </summary>
    internal static bool Combina(string razaoSocial, string dominio)
    {
        string[] genericos =
        {
            "LTDA", "SA", "EIRELI", "ME", "EPP", "TECNOLOGIA", "SERVICOS", "SERVICO",
            "SOLUCOES", "SOLUCAO", "COMERCIO", "INDUSTRIA", "PARTICIPACOES", "HOLDING",
            "GRUPO", "BRASIL", "DO", "DA", "DE", "E", "EM", "CONSULTORIA", "SISTEMAS",
            "SOFTWARE", "INFORMATICA", "DIGITAL", "GESTAO", "ADMINISTRACAO", "MEDICOS",
            "MEDICA", "CLINICA", "ASSESSORIA", "EMPREENDIMENTOS", "COMERCIAL"
        };

        var rotulo = Util.NomeEmpresa.Chave(dominio.Split('.')[0]).Replace(" ", "");
        if (rotulo.Length < 3) return false;

        var palavras = Util.NomeEmpresa.Chave(razaoSocial)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Length >= 3 && !genericos.Contains(p))
            .ToList();
        if (palavras.Count == 0) return false;

        // Palavra relevante do nome dentro do domínio, ou domínio dentro do nome colado.
        var nomeColado = string.Concat(palavras);
        return palavras.Any(p => rotulo.Contains(p)) || nomeColado.Contains(rotulo);
    }

    /// <summary>Domínio do e-mail no campo Contato ("... | E-mail: x@y.com.br | ..."),
    /// ou null quando não há e-mail ou ele é de provedor gratuito.</summary>
    internal static string? DominioDoContato(string? contato)
    {
        if (string.IsNullOrWhiteSpace(contato)) return null;

        var marca = contato.IndexOf("E-mail:", StringComparison.OrdinalIgnoreCase);
        if (marca < 0) return null;

        var trecho = contato[(marca + 7)..].Split('|')[0].Trim();
        var arroba = trecho.IndexOf('@');
        if (arroba < 0 || arroba == trecho.Length - 1) return null;

        var dominio = trecho[(arroba + 1)..].Trim().Trim('.', ',', ';').ToLowerInvariant();
        if (dominio.Length < 4 || !dominio.Contains('.')) return null;
        if (Gratuitos.Any(g => dominio.StartsWith(g))) return null;
        if (dominio.Any(ch => char.IsWhiteSpace(ch))) return null;

        return dominio;
    }
}
