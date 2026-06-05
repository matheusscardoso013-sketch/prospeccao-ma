using System.Text;
using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Data;

namespace Prospeccao.Web.Ingestao;

/// <summary>Resumo de uma importação de CNPJs.</summary>
public class ResultadoImportacao
{
    public int Importados { get; set; }
    public int JaExistiam { get; set; }
    public int NaoEncontrados { get; set; }
    public int Invalidos { get; set; }
    public List<string> Mensagens { get; } = new();
}

/// <summary>
/// Importa CNPJs reais (a partir de um texto/CSV com um CNPJ por linha), enriquece
/// cada um via BrasilAPI e grava como Lead. Deduplica por CNPJ (chave natural).
/// </summary>
public class ImportadorCnpj
{
    private readonly AppDbContext _db;
    private readonly ConectorBrasilApi _brasilApi;
    private readonly ILogger<ImportadorCnpj> _log;

    public ImportadorCnpj(AppDbContext db, ConectorBrasilApi brasilApi, ILogger<ImportadorCnpj> log)
    {
        _db = db;
        _brasilApi = brasilApi;
        _log = log;
    }

    public async Task<ResultadoImportacao> ImportarAsync(string conteudo, CancellationToken ct = default)
    {
        var resultado = new ResultadoImportacao();

        // 1) Extrai e normaliza os CNPJs (14 dígitos), descartando inválidos e duplicados.
        var cnpjs = ExtrairCnpjs(conteudo, resultado);
        if (cnpjs.Count == 0)
        {
            resultado.Mensagens.Add("Nenhum CNPJ válido encontrado no conteúdo enviado.");
            return resultado;
        }

        // 2) Descobre quais já existem no banco (dedup contra a base).
        var existentes = (await _db.Leads
            .Where(l => cnpjs.Contains(l.Cnpj))
            .Select(l => l.Cnpj)
            .ToListAsync(ct)).ToHashSet();

        // 3) Enriquece e grava os novos, respeitando rate limit (pausa entre chamadas).
        foreach (var cnpj in cnpjs)
        {
            if (existentes.Contains(cnpj))
            {
                resultado.JaExistiam++;
                continue;
            }

            var lead = await _brasilApi.EnriquecerAsync(cnpj, ct);
            if (lead is null)
            {
                resultado.NaoEncontrados++;
                resultado.Mensagens.Add($"CNPJ {cnpj}: não encontrado ou falha na consulta.");
                continue;
            }

            _db.Leads.Add(lead);
            resultado.Importados++;
            existentes.Add(cnpj); // evita duplicar se vier repetido depois

            await Task.Delay(300, ct); // gentileza com o rate limit da BrasilAPI
        }

        await _db.SaveChangesAsync(ct);
        _log.LogInformation("Importação concluída: {Imp} novos, {Ja} já existiam, {Nf} não encontrados",
            resultado.Importados, resultado.JaExistiam, resultado.NaoEncontrados);
        return resultado;
    }

    /// <summary>Pega só os dígitos de cada linha; aceita CNPJ formatado ou não. Valida 14 dígitos.</summary>
    private static List<string> ExtrairCnpjs(string conteudo, ResultadoImportacao resultado)
    {
        var vistos = new HashSet<string>();
        var lista = new List<string>();

        foreach (var linha in conteudo.Split('\n', '\r', ';', ',', '\t'))
        {
            var bruto = linha.Trim();
            if (bruto.Length == 0) continue;

            var sb = new StringBuilder(14);
            foreach (var c in bruto)
                if (char.IsDigit(c)) sb.Append(c);

            var digitos = sb.ToString();
            if (digitos.Length == 0) continue; // linha sem dígitos (ex.: cabeçalho)

            if (digitos.Length != 14)
            {
                resultado.Invalidos++;
                resultado.Mensagens.Add($"'{bruto}' ignorado: não tem 14 dígitos.");
                continue;
            }

            if (vistos.Add(digitos))
                lista.Add(digitos);
        }
        return lista;
    }
}
