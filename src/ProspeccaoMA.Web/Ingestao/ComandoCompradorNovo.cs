using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Matching;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Cadastra um comprador vindo de reunião e o cruza com a base — o caminho completo do
/// "o diretor conversou com alguém hoje" até "estas são as empresas para levar a ele".
///
/// O motor normal trabalha na direção oposta: pega UM alvo e procura compradores. Aqui é
/// preciso o inverso — um comprador novo contra milhares de alvos. Cruzar tudo com IA seria
/// inviável (uma chamada por alvo), então a triagem usa EMBEDDINGS, que têm cota separada
/// e muito mais folgada: ranqueia todos os alvos por similaridade com a tese e só os
/// finalistas vão à IA. Uso:
///   cadastrar-comprador arquivo.json [--gravar]
///   cruzar-comprador "Nome" [--max 15] [--gravar]
/// </summary>
public static class ComandoCompradorNovo
{
    public static async Task CadastrarAsync(IServiceProvider sp, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Uso: cadastrar-comprador <arquivo.json> [--gravar]"); return; }
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));

        var json = await File.ReadAllTextAsync(args[1]);
        var dados = JsonSerializer.Deserialize<CompradorJson>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        var existente = await db.Compradores.FirstOrDefaultAsync(c => c.Nome == dados.Nome);
        var c = existente ?? new Comprador { Nome = dados.Nome };

        c.RazaoSocial = dados.RazaoSocial ?? c.RazaoSocial;
        c.TipoEmpresa = dados.TipoEmpresa ?? c.TipoEmpresa;
        c.Segmento = dados.Segmento ?? c.Segmento;
        c.Site = dados.Site ?? c.Site;
        c.Responsavel = dados.Responsavel ?? c.Responsavel;
        c.Contato = dados.Contato ?? c.Contato;
        c.Tags = dados.Tags ?? c.Tags;
        c.FaixaFaturamento = dados.FaixaFaturamento ?? c.FaixaFaturamento;
        c.Tese = dados.Tese ?? c.Tese;
        c.TipoOperacao = dados.TipoOperacao ?? c.TipoOperacao;
        c.GeografiaAlvo = dados.GeografiaAlvo ?? c.GeografiaAlvo;
        c.ModeloNegocioAlvo = dados.ModeloNegocioAlvo ?? c.ModeloNegocioAlvo;
        c.Exclusoes = dados.Exclusoes ?? c.Exclusoes;
        c.Cultura = dados.Cultura ?? c.Cultura;
        c.FaturamentoMinAlvo = dados.FaturamentoMinAlvo ?? c.FaturamentoMinAlvo;
        c.FaturamentoMaxAlvo = dados.FaturamentoMaxAlvo ?? c.FaturamentoMaxAlvo;
        c.MargemEbitdaMinima = dados.MargemEbitdaMinima ?? c.MargemEbitdaMinima;
        c.Ativo = true;

        Console.WriteLine($"{(existente is null ? "NOVO" : "ATUALIZA")}: {c.Nome}");
        Console.WriteLine($"  tipo: {c.TipoEmpresa} | segmento: {c.Segmento} | resp.: {c.Responsavel}");
        Console.WriteLine($"  tese: {c.Tese.Length} caracteres");
        Console.WriteLine($"  faixa: {(c.FaturamentoMinAlvo is null ? "sem piso" : c.FaturamentoMinAlvo.Value.ToString("C0"))} " +
                          $"a {(c.FaturamentoMaxAlvo is null ? "sem teto" : c.FaturamentoMaxAlvo.Value.ToString("C0"))}");
        Console.WriteLine($"  exclusões: {(c.Exclusoes ?? "(nenhuma)")[..Math.Min(120, (c.Exclusoes ?? "(nenhuma)").Length)]}");

        if (!gravar) { Console.WriteLine("\nDRY-RUN — nada gravado. Use --gravar."); return; }

        if (existente is null) db.Compradores.Add(c);
        await db.SaveChangesAsync();
        Console.WriteLine($"\nGravado (id {c.Id}).");
    }

    public static async Task CruzarAsync(IServiceProvider sp, string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("Uso: cruzar-comprador \"Nome\" [--max 15] [--gravar]"); return; }
        var nome = args[1];
        var gravar = args.Any(a => a.Equals("--gravar", StringComparison.OrdinalIgnoreCase));
        var max = 15;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--max", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var m)) max = m;

        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();
        var ia = escopo.ServiceProvider.GetRequiredService<IClassificadorIA>();
        var motor = escopo.ServiceProvider.GetRequiredService<IMotorSinergia>();

        var c = await db.Compradores.FirstOrDefaultAsync(x => x.Nome.Contains(nome));
        if (c is null) { Console.WriteLine($"Comprador não encontrado: {nome}"); return; }
        Console.WriteLine($"Comprador: {c.Nome} (tese {c.Tese.Length} chars)\n");

        // 1) Embedding da tese — a régua contra a qual todos os alvos serão medidos.
        // Reaproveita o que está no banco: regerar a cada execução desperdiça cota e, pior,
        // trava o comando inteiro quando a cota do dia acabou (visto em 12/08).
        float[]? vetorTese = null;
        var hashTese = MotorSinergia.HashTese(c);
        if (c.TeseEmbedding is not null && c.TeseEmbeddingHash == hashTese)
        {
            try { vetorTese = JsonSerializer.Deserialize<float[]>(c.TeseEmbedding); } catch { }
            if (vetorTese is { Length: > 0 }) Console.WriteLine("Embedding da tese: reaproveitado do banco.");
        }
        if (vetorTese is null or { Length: 0 })
        {
            vetorTese = await ia.GerarEmbeddingAsync(MotorSinergia.TextoTese(c));
            if (vetorTese is null) { Console.WriteLine("Não consegui gerar o embedding da tese (cota?)."); return; }
            c.TeseEmbedding = JsonSerializer.Serialize(vetorTese);
            c.TeseEmbeddingHash = hashTese;
            await db.SaveChangesAsync();
            Console.WriteLine("Embedding da tese gerado.");
        }

        // 2) Alvos candidatos: os que já têm vetor entram de graça; os demais são gerados
        //    até o orçamento (cota de embedding é separada da de geração, bem mais folgada).
        var jaCruzados = await db.SinergiasComprador.Where(s => s.CompradorId == c.Id)
            .Select(s => s.LeadId).ToListAsync();

        // --cnae: recorta o universo antes de gastar embedding. Vale quando a tese é
        // explícita sobre o QUE a empresa faz — a Starian compra empresas de SOFTWARE, e
        // uma clínica (CNAE 86) é cliente de software, não alvo dela. Sem o recorte,
        // vetorizar 5 mil alvos leva ~1h e a maioria nunca teria chance.
        var prefixos = Texto(args, "--cnae")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var query = db.Leads.Where(l => !jaCruzados.Contains(l.Id));
        if (prefixos is { Length: > 0 })
        {
            // Alvos curados não têm CNAE — entram sempre (são a carteira da própria Valore).
            query = query.Where(l => l.Cnae == "" || l.Cnae == null || prefixos.Any(p => l.Cnae.StartsWith(p)));
            Console.WriteLine($"Recorte por CNAE: {string.Join(",", prefixos)} (+ curados sem CNAE)");
        }
        var leads = await query.ToListAsync();
        Console.WriteLine($"Alvos a avaliar: {leads.Count}");

        // O orçamento limita apenas embeddings NOVOS. Quem já tem vetor no banco entra de
        // graça — é o que permite rodar de novo e continuar de onde parou, em vez de gastar
        // a cota sempre nos mesmos primeiros alvos.
        var comVetor = new List<(Lead L, double Sim)>();
        int gerados = 0;
        // --orcamento 0 usa SÓ o que já está em cache (útil quando a cota de embedding
        // do dia acabou — descoberto em 12/08: ela NÃO é ilimitada, 429 após ~1.000).
        var orcamento = int.TryParse(Texto(args, "--orcamento"), out var o) ? o : 1500;
        foreach (var l in leads)
        {
            var tinhaVetor = l.TextoEmbedding is not null
                             && l.TextoEmbeddingHash == MotorSinergia.HashLead(l);
            if (!tinhaVetor && orcamento <= 0) continue; // sem cota p/ este; fica para a próxima

            var v = await motor.EmbeddingDoLeadAsync(l);
            if (v is null) continue;
            comVetor.Add((l, MotorSinergia.Similaridade(vetorTese, v)));

            if (tinhaVetor) continue;
            gerados++;
            orcamento--;
            if (gerados % 200 == 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"  ...{gerados} novo(s) vetor(es), {comVetor.Count}/{leads.Count} prontos");
            }
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"Vetorizados: {comVetor.Count}/{leads.Count} ({gerados} gerados agora).");

        var finalistas = comVetor.OrderByDescending(x => x.Sim).Take(max).ToList();
        Console.WriteLine($"\n=== {finalistas.Count} finalistas por similaridade ===");
        foreach (var f in finalistas)
            Console.WriteLine($"  {f.Sim:0.000}  {f.L.RazaoSocial}  ({Util.CnaeCatalogo.Rotulo(f.L.Cnae)})");

        if (!gravar) { Console.WriteLine("\nDRY-RUN — a IA não foi chamada. Use --gravar para pontuar."); return; }

        Console.WriteLine("\n=== Pontuação pela IA ===");
        var novos = 0;
        foreach (var (lead, _) in finalistas)
        {
            if (GeminiClassificador.GeracaoSuspensa) { Console.WriteLine("Freio de cota — o resto fica para depois."); break; }

            var r = await ia.ClassificarSinergiaAsync(lead, c);
            if (r.Score == 0) { Console.WriteLine($"  (IA indisponível em {lead.RazaoSocial})"); break; }

            var s = new SinergiaComprador { LeadId = lead.Id, CompradorId = c.Id };
            MotorSinergia.AplicarResultado(s, r);
            db.SinergiasComprador.Add(s);
            await db.SaveChangesAsync();
            novos++;

            Console.WriteLine($"\n  [{r.Score}] {lead.RazaoSocial}");
            if (r.Setor is not null)
                Console.WriteLine($"        setor {r.Setor}/40 · porte {r.Porte}/25 · modelo {r.Modelo}/20 · geo {r.Geo}/15");
            Console.WriteLine($"        {r.Racional}");
        }
        Console.WriteLine($"\n{novos} par(es) gravado(s) para {c.Nome}.");
    }

    private static string? Texto(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i < 0 || i + 1 >= args.Length ? null : args[i + 1];
    }

    private class CompradorJson
    {
        public string Nome { get; set; } = "";
        public string? RazaoSocial { get; set; }
        public string? TipoEmpresa { get; set; }
        public string? Segmento { get; set; }
        public string? Site { get; set; }
        public string? Responsavel { get; set; }
        public string? Contato { get; set; }
        public string? Tags { get; set; }
        public string? FaixaFaturamento { get; set; }
        public string? Tese { get; set; }
        public string? TipoOperacao { get; set; }
        public string? GeografiaAlvo { get; set; }
        public string? ModeloNegocioAlvo { get; set; }
        public string? Exclusoes { get; set; }
        public string? Cultura { get; set; }
        public decimal? FaturamentoMinAlvo { get; set; }
        public decimal? FaturamentoMaxAlvo { get; set; }
        public decimal? MargemEbitdaMinima { get; set; }
    }
}
