using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

public record ResultadoImportacao(int Recebidos, int Validos, int Novos, int Atualizados, int Falhas);

public interface IImportadorCnpj
{
    Task<ResultadoImportacao> ImportarAsync(IEnumerable<string> cnpjs, CancellationToken ct = default);
}

/// <summary>
/// Importa uma lista de CNPJs REAIS, enriquece cada um via BrasilAPI e grava/atualiza
/// em Leads. Idempotente por CNPJ (índice único). Não inventa nenhum dado: o que a API
/// não trouxer fica vazio ("não consta no cadastro" na tela).
/// </summary>
public class ImportadorCnpj : IImportadorCnpj
{
    private readonly AppDbContext _db;
    private readonly IConectorBrasilApi _brasilApi;
    private readonly ILogger<ImportadorCnpj> _log;

    private const string FonteReceita = "Receita Federal — base pública (via BrasilAPI)";

    public ImportadorCnpj(AppDbContext db, IConectorBrasilApi brasilApi, ILogger<ImportadorCnpj> log)
    {
        _db = db;
        _brasilApi = brasilApi;
        _log = log;
    }

    public async Task<ResultadoImportacao> ImportarAsync(IEnumerable<string> cnpjs, CancellationToken ct = default)
    {
        var recebidos = 0;
        var validos = new List<string>();
        foreach (var c in cnpjs)
        {
            recebidos++;
            var n = CnpjUtil.Normalizar(c);
            if (n is not null && !validos.Contains(n))
                validos.Add(n);
        }

        int novos = 0, atualizados = 0, falhas = 0;

        foreach (var cnpj in validos)
        {
            ct.ThrowIfCancellationRequested();

            EmpresaBrasilApi? dados;
            try
            {
                dados = await _brasilApi.ConsultarAsync(cnpj, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Erro ao enriquecer CNPJ {Cnpj}", cnpj);
                falhas++;
                continue;
            }

            if (dados is null)
            {
                _log.LogWarning("CNPJ {Cnpj} sem retorno na BrasilAPI — ignorado", cnpj);
                falhas++;
                continue;
            }

            var existente = await _db.Leads.FirstOrDefaultAsync(l => l.Cnpj == cnpj, ct);
            if (existente is not null && existente.EditadoManualmente)
            {
                atualizados++; // ajustado à mão na plataforma — não sobrescreve
                continue;
            }
            var lead = existente ?? new Lead { Cnpj = cnpj };

            lead.RazaoSocial = dados.RazaoSocial?.Trim() ?? lead.RazaoSocial;
            lead.Cnae = dados.CnaeFiscal?.ToString() ?? lead.Cnae;
            lead.Uf = dados.Uf?.Trim() ?? lead.Uf;
            lead.Municipio = dados.Municipio?.Trim() ?? lead.Municipio;
            lead.CapitalSocial = dados.CapitalSocial ?? lead.CapitalSocial;
            lead.Situacao = dados.DescricaoSituacaoCadastral?.Trim() ?? lead.Situacao;
            lead.PorteEstimado = EstimadorPorte.Estimar(lead.CapitalSocial, dados.Porte);
            lead.Contato = MontarContato(dados);

            if (existente is null)
            {
                _db.Leads.Add(lead);
                novos++;
            }
            else
            {
                atualizados++;
            }
        }

        await _db.SaveChangesAsync(ct);
        var resultado = new ResultadoImportacao(recebidos, validos.Count, novos, atualizados, falhas);
        _log.LogInformation("Importação concluída: {@Resultado}", resultado);
        return resultado;
    }

    private static string? MontarContato(EmpresaBrasilApi d)
    {
        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.DddTelefone1)) partes.Add($"Tel: {d.DddTelefone1.Trim()}");
        if (!string.IsNullOrWhiteSpace(d.Email)) partes.Add($"E-mail: {d.Email.Trim()}");

        var endereco = string.Join(", ", new[] { d.Logradouro, d.Numero, d.Bairro, d.Municipio, d.Uf }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        if (!string.IsNullOrWhiteSpace(endereco)) partes.Add($"End: {endereco}");

        return partes.Count > 0 ? string.Join(" | ", partes) : null;
    }
}
