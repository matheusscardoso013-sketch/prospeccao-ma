using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;
using ProspeccaoMA.Web.Util;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Raio-x da plataforma pelo console, sem abrir o painel: últimas rodadas, tamanho da base,
/// cobertura do dado rico e o funil da mesa. Só lê — nunca grava. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- status
/// </summary>
public static class ComandoStatus
{
    public static async Task ExecutarAsync(IServiceProvider sp)
    {
        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<AppDbContext>();

        Console.WriteLine($"=== Prospecção M&A — status em {Fuso.Brasil(DateTime.UtcNow):dd/MM/yyyy HH:mm} (Brasília) ===\n");

        Console.WriteLine("-- Últimas rodadas --");
        var rodadas = await db.ExecucoesJob.OrderByDescending(e => e.IniciadoEm).Take(7).ToListAsync();
        foreach (var r in rodadas)
        {
            var dur = r.FinalizadoEm is null ? "em andamento" : $"{(r.FinalizadoEm.Value - r.IniciadoEm).TotalMinutes:0.#} min";
            Console.WriteLine($"  {Fuso.Brasil(r.IniciadoEm):dd/MM HH:mm} | {r.Status,-11} | {r.LeadsGerados} lead(s) | {dur}" +
                              (string.IsNullOrWhiteSpace(r.Erro) ? "" : $" | ERRO: {Curto(r.Erro, 120)}"));
        }
        if (rodadas.Count == 0) Console.WriteLine("  (nenhuma rodada registrada)");

        // O recorte que decide QUEM entra na esteira. Sem isso à vista, é fácil olhar só a
        // qualidade do match e não perceber que o problema está em quem foi escolhido.
        Console.WriteLine("\n-- Configuração da esteira (quem entra) --");
        var configs = await db.Configuracoes.Where(c => c.Ativo).ToListAsync();
        foreach (var c in configs)
            Console.WriteLine($"  [{c.Id}] UFs: {(string.IsNullOrWhiteSpace(c.Ufs) ? "todas" : c.Ufs)} | CNAEs: {(string.IsNullOrWhiteSpace(c.Cnaes) ? "todos" : Curto(c.Cnaes, 60))}\n" +
                              $"        capital: {(c.CapitalMin is null ? "sem piso" : c.CapitalMin.Value.ToString("C0"))} " +
                              $"a {(c.CapitalMax is null ? "SEM TETO" : c.CapitalMax.Value.ToString("C0"))}");
        if (configs.Count == 0) Console.WriteLine("  (nenhuma configuração ativa)");

        var faixas = new (string Rotulo, decimal Min, decimal Max)[]
        {
            ("até R$ 1 mi", 0, 1_000_000), ("R$ 1-10 mi", 1_000_000, 10_000_000),
            ("R$ 10-50 mi", 10_000_000, 50_000_000), ("R$ 50-200 mi", 50_000_000, 200_000_000),
            ("acima de R$ 200 mi", 200_000_000, decimal.MaxValue)
        };
        Console.WriteLine($"  {"faixa de capital social",-22} {"na base",8} {"prospectadas",13}");
        foreach (var f in faixas)
        {
            var naBase = await db.Leads.CountAsync(l => l.Origem == Lead.OrigemReceita
                && l.CapitalSocial > f.Min && l.CapitalSocial <= f.Max);
            var comScore = await db.Leads.CountAsync(l => l.Scores.Count > 0
                && l.CapitalSocial > f.Min && l.CapitalSocial <= f.Max);
            Console.WriteLine($"    {f.Rotulo,-20} {naBase,8} {comScore,13}");
        }

        Console.WriteLine("\n-- Base --");
        var leadsReceita = await db.Leads.CountAsync(l => l.Origem == Lead.OrigemReceita);
        var leadsValore = await db.Leads.CountAsync(l => l.Origem == Lead.OrigemValore);
        var compradores = await db.Compradores.CountAsync(c => c.Ativo);
        var semTese = await db.Compradores.CountAsync(c => c.Ativo && c.Tese.Length < 20);
        Console.WriteLine($"  Empresas Receita: {leadsReceita}   |   Alvos Valore: {leadsValore}   |   Compradores ativos: {compradores} ({semTese} sem tese)");

        Console.WriteLine("\n-- Dado rico (cobertura) --");
        var criteriosOk = await db.Compradores.CountAsync(c => c.Ativo && c.CriteriosExtraidosEm != null);
        var criteriosValidados = await db.Compradores.CountAsync(c => c.Ativo && c.CriteriosValidados);
        var embeddings = await db.Compradores.CountAsync(c => c.Ativo && c.TeseEmbedding != null);
        var perfis = await db.Compradores.CountAsync(c => c.Ativo && c.PerfilSite != null);
        Console.WriteLine($"  Critérios extraídos pela IA: {criteriosOk}/{compradores}   |   validados pelo time: {criteriosValidados}");
        Console.WriteLine($"  Embeddings da tese: {embeddings}/{compradores}   |   Perfis do site: {perfis}/{compradores}");
        var ultimaExtracao = await db.Compradores.MaxAsync(c => c.CriteriosExtraidosEm);
        var faltamCriterios = await db.Compradores.CountAsync(c => c.Ativo && c.Tese.Length >= 20 && c.CriteriosExtraidosEm == null);
        Console.WriteLine($"  Faltam extrair: {faltamCriterios}   |   última extração: " +
                          (ultimaExtracao is null ? "nunca" : $"{Fuso.Brasil(ultimaExtracao.Value):dd/MM/yyyy HH:mm}"));

        // O poço secou? Lead sem nenhum par nunca foi confrontado com a base de compradores —
        // é matéria-prima ainda intocada. Se esse número for baixo, a esteira precisa de
        // leads novos; se for alto, o gargalo é vazão, não falta de empresa.
        Console.WriteLine("\n-- Cobertura da base (quanto ainda há para prospectar) --");
        var cruzados = await db.SinergiasComprador.Select(s => s.LeadId).Distinct().CountAsync();
        var totalLeads = await db.Leads.CountAsync();
        var receitaCruzados = await db.Leads
            .CountAsync(l => l.Origem == Lead.OrigemReceita && l.Scores.Count > 0);
        Console.WriteLine($"  Alvos já cruzados com compradores: {cruzados}/{totalLeads} " +
                          $"({(totalLeads == 0 ? 0 : 100.0 * cruzados / totalLeads):0.#}%)");
        Console.WriteLine($"  NUNCA cruzados (estoque intocado): {totalLeads - cruzados}");
        Console.WriteLine($"  Empresas da Receita já pontuadas na esteira: {receitaCruzados}");

        Console.WriteLine("\n-- Funil da mesa --");
        var funil = await db.SinergiasComprador
            .GroupBy(s => s.Status)
            .Select(g => new { Status = g.Key, Qtd = g.Count() })
            .ToListAsync();
        foreach (StatusSinergia st in Enum.GetValues<StatusSinergia>())
            Console.WriteLine($"  {st,-13}: {funil.FirstOrDefault(f => f.Status == st)?.Qtd ?? 0}");

        var quentes = await db.SinergiasComprador.CountAsync(s => s.Score >= 80 && s.Status == StatusSinergia.Novo);
        var mornos = await db.SinergiasComprador.CountAsync(s => s.Score >= 60 && s.Score < 80 && s.Status == StatusSinergia.Novo);
        Console.WriteLine($"\n  Oportunidades quentes paradas (>=80, ainda 'Novo'): {quentes}");
        Console.WriteLine($"  Mornas paradas (60-79, ainda 'Novo'): {mornos}");

        var ultimoMovimento = await db.SinergiasComprador
            .Where(s => s.AtualizadoEm != null)
            .MaxAsync(s => (DateTime?)s.AtualizadoEm);
        Console.WriteLine(ultimoMovimento is null
            ? "  Último movimento do time na mesa: NUNCA"
            : $"  Último movimento do time na mesa: {Fuso.Brasil(ultimoMovimento.Value):dd/MM/yyyy HH:mm}");

        Console.WriteLine("\n-- Produção dos últimos 7 dias --");
        var corte = DateTime.UtcNow.AddDays(-7);
        var paresRecentes = await db.SinergiasComprador.CountAsync(s => s.GeradoEm >= corte);
        var scoresRecentes = await db.LeadScores.CountAsync(s => s.GeradoEm >= corte);
        Console.WriteLine($"  Leads pontuados: {scoresRecentes}   |   Pares alvo×comprador avaliados: {paresRecentes}");
    }

    private static string Curto(string? s, int n)
        => string.IsNullOrWhiteSpace(s) ? "" : (s.Length > n ? s[..n] + "…" : s);
}
