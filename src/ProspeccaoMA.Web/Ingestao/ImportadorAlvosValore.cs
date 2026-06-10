using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

public record ResultadoImportAlvos(int LinhasSellSide, int Novos, int Atualizados);

public interface IImportadorAlvosValore
{
    Task<ResultadoImportAlvos> ImportarAsync(string caminhoXlsx, CancellationToken ct = default);
}

/// <summary>
/// Importa os alvos SELL-SIDE curados da base da Valore (planilha, linhas "Sell-Side")
/// como Leads de Origem "Base Valore". São empresas reais já trabalhadas pela boutique;
/// a planilha não traz CNPJ (campo fica nulo — nunca inventamos) nem UF estruturada.
/// A riqueza está em Segmento (H), Resumo M&A (M) e Descrição (P), que alimentam o
/// matching com compradores. Idempotente por RazaoSocial dentro da origem Valore.
/// Mapeia: A=Nome, B=Interesse, D=RazaoSocial, E=Contato, G=Tipo, H=Segmento,
/// K=FaixaFaturamento (→ PorteEstimado com "~"), M=Resumo, P=Descrição.
/// </summary>
public class ImportadorAlvosValore : IImportadorAlvosValore
{
    private readonly AppDbContext _db;
    private readonly ILogger<ImportadorAlvosValore> _log;

    public ImportadorAlvosValore(AppDbContext db, ILogger<ImportadorAlvosValore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<ResultadoImportAlvos> ImportarAsync(string caminhoXlsx, CancellationToken ct = default)
    {
        if (!File.Exists(caminhoXlsx))
            throw new FileNotFoundException($"Planilha não encontrada: {caminhoXlsx}");

        var linhas = XlsxLeitor.LerLinhas(caminhoXlsx, "BaseDados");

        var linhasSell = 0;
        var porNome = new Dictionary<string, Lead>(StringComparer.OrdinalIgnoreCase);

        foreach (var cels in linhas)
        {
            var interesse = XlsxLeitor.Get(cels, "B");
            if (interesse is null || !interesse.Contains("Sell", StringComparison.OrdinalIgnoreCase)) continue;

            var nome = XlsxLeitor.Get(cels, "A");
            if (nome is null) continue;
            linhasSell++;

            var razao = XlsxLeitor.Get(cels, "D") ?? nome;
            var faixaFat = XlsxLeitor.Get(cels, "K");
            var descricao = MontarDescricao(XlsxLeitor.Get(cels, "G"), XlsxLeitor.Get(cels, "M"), XlsxLeitor.Get(cels, "P"));

            porNome[razao] = new Lead
            {
                Cnpj = null, // a planilha não traz CNPJ; não inventamos
                RazaoSocial = razao,
                Segmento = XlsxLeitor.Get(cels, "H"),
                Contato = XlsxLeitor.Get(cels, "E"),
                PorteEstimado = faixaFat is null ? "~ faturamento não informado" : $"~ {faixaFat}",
                Descricao = descricao,
                Origem = Lead.OrigemValore,
                Situacao = string.Empty // não entra no fluxo diário da Receita (que exige ATIVA)
            };
        }

        int novos = 0, atualizados = 0;
        foreach (var (razao, novo) in porNome)
        {
            ct.ThrowIfCancellationRequested();
            var existente = await _db.Leads.FirstOrDefaultAsync(
                l => l.Origem == Lead.OrigemValore && l.RazaoSocial == razao, ct);

            if (existente is null)
            {
                _db.Leads.Add(novo);
                novos++;
            }
            else
            {
                existente.Segmento = novo.Segmento;
                existente.Contato = novo.Contato;
                existente.PorteEstimado = novo.PorteEstimado;
                existente.Descricao = novo.Descricao;
                atualizados++;
            }
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Alvos Valore importados: {Novos} novo(s), {Atu} atualizado(s) de {Linhas} linha(s) sell-side",
            novos, atualizados, linhasSell);
        return new ResultadoImportAlvos(linhasSell, novos, atualizados);
    }

    private static string? MontarDescricao(string? tipo, string? resumo, string? descricao)
    {
        var partes = new List<string>();
        if (tipo is not null) partes.Add($"Tipo: {tipo}");
        if (resumo is not null) partes.Add(resumo);
        if (descricao is not null && descricao != resumo) partes.Add(descricao);
        var texto = string.Join("\n", partes).Trim();
        if (texto.Length == 0) return null;
        return texto.Length > 2000 ? texto[..2000] + "…" : texto;
    }
}
