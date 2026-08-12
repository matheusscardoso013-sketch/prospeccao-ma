namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Comando de console para importar o recorte da Receita (os arquivos oficiais são enormes,
/// então isso roda fora do fluxo web):
///   dotnet run -- importar-receita "C:\caminho\dados" --cnaes 4646,2110 --ufs SP,MG [--gravar]
/// Sem --gravar é dry-run (só conta, não escreve).
/// </summary>
public static class ComandoImportarReceita
{
    public static async Task ExecutarAsync(IServiceProvider sp, string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Uso: importar-receita <pasta> --cnaes 4646,2110 --ufs SP,MG [--porte 05] [--capmin N] [--capmax N] [--gravar]");
            return;
        }

        var pasta = args[1];
        var cnaes = ValorLista(args, "--cnaes");
        var ufs = ValorLista(args, "--ufs");
        var gravar = args.Any(a => string.Equals(a, "--gravar", StringComparison.OrdinalIgnoreCase));
        var capMin = ValorDecimal(args, "--capmin");
        var capMax = ValorDecimal(args, "--capmax");
        var portes = ValorLista(args, "--porte");
        var naturezas = ValorLista(args, "--natureza"); // 2062=Ltda, 2054/2046=S.A., 2232/2240=simples, 2305=EIRELI // 05 = "demais" = faturamento acima de R$ 4,8 mi

        Console.WriteLine($"Importando recorte da Receita de: {pasta}");
        Console.WriteLine($"  CNAEs: {string.Join(",", cnaes)} | UFs: {string.Join(",", ufs)} | capital: {capMin}–{capMax} | porte: {(portes.Count == 0 ? "todos" : string.Join(",", portes))} | gravar: {gravar}");

        using var escopo = sp.CreateScope();
        var importador = escopo.ServiceProvider.GetRequiredService<IImportadorReceita>();
        var r = await importador.ImportarRecorteAsync(pasta, cnaes, ufs, gravar, capMin, capMax, portes, naturezas);

        Console.WriteLine($"Estabelecimentos lidos: {r.LinhasEstabelecimentos}");
        Console.WriteLine($"Selecionados no recorte: {r.Selecionados}");
        Console.WriteLine(r.Gravou
            ? $"Gravados: {r.Novos} novo(s), {r.Atualizados} atualizado(s)."
            : "Dry-run (use --gravar para escrever no banco).");
    }

    private static List<string> ValorLista(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return new();
        return args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static decimal? ValorDecimal(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        return decimal.TryParse(args[i + 1], System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
