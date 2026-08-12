using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Ingestao;

public record ResultadoImportacaoReceita(
    int LinhasEstabelecimentos, int Selecionados, int Novos, int Atualizados, bool Gravou);

public interface IImportadorReceita
{
    Task<ResultadoImportacaoReceita> ImportarRecorteAsync(
        string pasta, IReadOnlyCollection<string> cnaes, IReadOnlyCollection<string> ufs,
        bool gravar, decimal? capMin = null, decimal? capMax = null,
        IReadOnlyCollection<string>? portes = null, IReadOnlyCollection<string>? naturezas = null,
        CancellationToken ct = default);
}

/// <summary>
/// Importa um RECORTE da base pública da Receita (descoberta por setor). Lê os CSVs
/// oficiais em streaming e filtra por CNAE principal, UF e situação ATIVA — só os dados
/// REAIS que casam entram. Faz join Estabelecimentos × Empresas por CNPJ básico para obter
/// razão social, capital e porte, e resolve o nome do município pelo arquivo de Municípios.
/// Idempotente por CNPJ. Pensado para rodar como comando de console (arquivos são enormes).
/// </summary>
public class ImportadorReceita : IImportadorReceita
{
    private readonly AppDbContext _db;
    private readonly ILogger<ImportadorReceita> _log;

    private const string Fonte = "Receita Federal — base pública";
    private const string SituacaoAtiva = "02"; // código de ATIVA no layout da Receita

    public ImportadorReceita(AppDbContext db, ILogger<ImportadorReceita> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<ResultadoImportacaoReceita> ImportarRecorteAsync(
        string pasta, IReadOnlyCollection<string> cnaes, IReadOnlyCollection<string> ufs,
        bool gravar, decimal? capMin = null, decimal? capMax = null,
        IReadOnlyCollection<string>? portes = null, IReadOnlyCollection<string>? naturezas = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(pasta))
            throw new DirectoryNotFoundException($"Pasta não encontrada: {pasta}");

        var cnaesDig = cnaes.Select(CsvReceita.SoDigitos).Where(c => c.Length > 0).ToHashSet();
        var ufsUp = ufs.Select(u => u.Trim().ToUpperInvariant()).Where(u => u.Length > 0).ToHashSet();

        var arquivos = Directory.GetFiles(pasta);
        string[] Filtrar(string marca) => arquivos
            .Where(a => Path.GetFileName(a).ToUpperInvariant().Contains(marca)).ToArray();

        var fEstab = Filtrar("ESTABELE");
        var fEmpre = Filtrar("EMPRE");
        var fMunic = Filtrar("MUNIC");

        if (fEstab.Length == 0) throw new FileNotFoundException("Nenhum arquivo de Estabelecimentos (…ESTABELE) na pasta.");

        var municipios = CarregarMunicipios(fMunic);

        // ----- Passo 1: varrer Estabelecimentos e selecionar os que casam o recorte -----
        var selecionados = new Dictionary<string, Lead>(); // chave: CNPJ completo (14)
        var basicosNecessarios = new HashSet<string>();
        var linhasEstab = 0;

        foreach (var arq in fEstab)
        {
            using var r = new StreamReader(arq, CsvReceita.Latin1);
            string? linha;
            while ((linha = await r.ReadLineAsync(ct)) is not null)
            {
                linhasEstab++;
                var c = CsvReceita.ParseLinha(linha);
                if (c.Length < 28) continue;

                var situacao = c[5].Trim();
                if (situacao != SituacaoAtiva) continue;

                var uf = c[19].Trim().ToUpperInvariant();
                if (ufsUp.Count > 0 && !ufsUp.Contains(uf)) continue;

                var cnae = CsvReceita.SoDigitos(c[11]);
                if (cnaesDig.Count > 0 && !cnaesDig.Any(f => cnae == f || cnae.StartsWith(f))) continue;

                var basico = c[0].Trim();
                var cnpj = basico + c[1].Trim() + c[2].Trim();
                if (cnpj.Length != 14) continue;

                // UMA linha por EMPRESA, não por estabelecimento. O arquivo da Receita traz
                // matriz e filiais como registros separados — sem isso, uma usina com 398
                // fazendas virava 398 "empresas" (visto em 29/07), inflando a base e
                // queimando cota de IA pontuando a mesma companhia dezenas de vezes.
                // Preferimos a matriz (c[3] == "1"); a filial só entra se a matriz não
                // casar o recorte (ex.: matriz em outra UF), para não perder a empresa.
                var ehMatriz = c[3].Trim() == "1";
                if (selecionados.TryGetValue(basico, out var jaEscolhido)
                    && !ehMatriz
                    && jaEscolhido.Cnpj!.Substring(8, 4) == "0001") continue;

                var munNome = municipios.TryGetValue(c[20].Trim(), out var nome) ? nome : c[20].Trim();

                selecionados[basico] = new Lead
                {
                    Cnpj = cnpj,
                    Cnae = cnae,
                    Uf = uf,
                    Municipio = munNome,
                    Situacao = "ATIVA",
                    Contato = MontarContato(c, munNome, uf)
                };
                basicosNecessarios.Add(basico);
            }
        }

        _log.LogInformation("Estabelecimentos lidos: {L}; selecionados no recorte: {S}", linhasEstab, selecionados.Count);

        // ----- Passo 2: varrer Empresas e completar razão social, capital e porte -----
        var dadosEmpresa = new Dictionary<string, (string razao, decimal capital, string porte, string natureza)>();
        foreach (var arq in fEmpre)
        {
            using var r = new StreamReader(arq, CsvReceita.Latin1);
            string? linha;
            while ((linha = await r.ReadLineAsync(ct)) is not null)
            {
                var c = CsvReceita.ParseLinha(linha);
                if (c.Length < 6) continue;
                var basico = c[0].Trim();
                if (!basicosNecessarios.Contains(basico)) continue;
                dadosEmpresa[basico] = (c[1].Trim(), CsvReceita.ParseCapital(c[4]), c[5].Trim(), c[2].Trim());
            }
        }

        foreach (var (cnpj, lead) in selecionados)
        {
            var basico = cnpj[..8];
            if (dadosEmpresa.TryGetValue(basico, out var d))
            {
                lead.RazaoSocial = d.razao;
                lead.CapitalSocial = d.capital;
                lead.PorteEstimado = EstimadorPorte.Estimar(d.capital, d.porte);
            }
            else
            {
                lead.RazaoSocial = string.IsNullOrWhiteSpace(lead.RazaoSocial) ? "(razão social não encontrada)" : lead.RazaoSocial;
                lead.PorteEstimado = EstimadorPorte.Estimar(0m, null);
            }
        }

        // NATUREZA JURÍDICA — separa SOCIEDADE de registro individual. O código 2135
        // ("Empresário Individual") é a razão social virar "CNPJ + nome da pessoa"; em 11/08
        // duas dessas entraram na esteira como se fossem empresas ("52.641.423 TIFFANY
        // GABRIELLI..."). Não são alvo de M&A: não há participação societária a vender.
        // Allow-list em vez de exclusão — na dúvida, fica de fora.
        if (naturezas is { Count: > 0 })
        {
            var codigos = naturezas.Select(x => x.Trim()).ToHashSet();
            var antesN = selecionados.Count;
            selecionados = selecionados
                .Where(kv => dadosEmpresa.TryGetValue(kv.Key[..8], out var d) && codigos.Contains(d.natureza))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            _log.LogInformation("Filtro de natureza jurídica: {Antes} → {Depois}", antesN, selecionados.Count);
        }

        // A Receita carimba o estado da empresa NA PRÓPRIA RAZÃO SOCIAL. "DIBUTE SOFTWARE
        // LTDA FALIDO" passou por todos os filtros em 11/08 porque a situação cadastral
        // ainda constava ATIVA. Empresa falida, em recuperação ou espólio não se vende.
        string[] marcasMortas = { "FALID", "EM RECUPERACAO", "EM RECUPERAÇÃO", "ESPOLIO", "ESPÓLIO", "EM LIQUIDACAO", "EM LIQUIDAÇÃO" };
        var antesM = selecionados.Count;
        selecionados = selecionados
            .Where(kv => !marcasMortas.Any(m => kv.Value.RazaoSocial.ToUpperInvariant().Contains(m)))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (antesM != selecionados.Count)
            _log.LogInformation("Filtro de empresa morta (falida/recuperação/espólio): {Antes} → {Depois}", antesM, selecionados.Count);

        // PORTE DECLARADO PELA RECEITA — o corte mais confiável que existe para o piso do
        // middle market. Os códigos seguem os limites legais de faturamento:
        //   01 = microempresa            (até R$ 360 mil/ano)
        //   03 = empresa de pequeno porte (até R$ 4,8 milhões/ano)
        //   05 = demais                   (ACIMA de R$ 4,8 milhões/ano)
        // Pedir porte 05 é pedir faturamento acima de R$ 4,8 mi sem depender de proxy —
        // é a propria empresa que declara. O TETO continua vindo do capital social, que é
        // proxy fraco, mas é o único número de tamanho que a base pública traz.
        if (portes is { Count: > 0 })
        {
            var codigos = portes.Select(x => x.Trim().PadLeft(2, '0')).ToHashSet();
            var antesP = selecionados.Count;
            selecionados = selecionados
                .Where(kv => dadosEmpresa.TryGetValue(kv.Key[..8], out var d)
                          && codigos.Contains(d.porte.PadLeft(2, '0')))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            _log.LogInformation("Filtro de porte [{Portes}]: {Antes} → {Depois}",
                string.Join(",", codigos), antesP, selecionados.Count);
        }

        // Filtro de capital (middle market): mantém o pool enxuto no Neon. Se a faixa for
        // informada, só entram empresas com capital REAL dentro dela (sem join confiável fica fora).
        if (capMin is not null || capMax is not null)
        {
            var antes = selecionados.Count;
            selecionados = selecionados
                .Where(kv => dadosEmpresa.ContainsKey(kv.Key[..8])
                          && (capMin is null || kv.Value.CapitalSocial >= capMin)
                          && (capMax is null || kv.Value.CapitalSocial <= capMax))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            _log.LogInformation("Filtro de capital [{Min}–{Max}]: {Antes} → {Depois}", capMin, capMax, antes, selecionados.Count);
        }

        if (!gravar)
        {
            // Distribuição por capital social do recorte selecionado. Ler os 71 milhões de
            // linhas leva minutos, então o dry-run entrega de uma vez o que é preciso para
            // calibrar a faixa — em vez de exigir uma passada por hipótese testada.
            (string Rotulo, decimal Min, decimal Max)[] faixas =
            {
                ("até R$ 100 mil", 0, 100_000), ("R$ 100-500 mil", 100_000, 500_000),
                ("R$ 500 mil-1 mi", 500_000, 1_000_000), ("R$ 1-5 mi", 1_000_000, 5_000_000),
                ("R$ 5-20 mi", 5_000_000, 20_000_000), ("R$ 20-50 mi", 20_000_000, 50_000_000),
                ("acima de R$ 50 mi", 50_000_000, decimal.MaxValue)
            };
            Console.WriteLine("\n  Distribuição por capital social do recorte:");
            foreach (var f in faixas)
                Console.WriteLine($"    {f.Rotulo,-20} {selecionados.Values.Count(l => l.CapitalSocial > f.Min && l.CapitalSocial <= f.Max),8}");
            Console.WriteLine();

            _log.LogInformation("Dry-run: {N} lead(s) seriam gravados (nada foi escrito).", selecionados.Count);
            return new ResultadoImportacaoReceita(linhasEstab, selecionados.Count, 0, 0, false);
        }

        // ----- Gravação idempotente por EMPRESA (CNPJ raiz) -----
        // Casar pelo CNPJ completo recriaria a linha quando o estabelecimento escolhido
        // mudasse entre importações (ex.: a base já tem a filial 0013 e agora a matriz
        // 0001 casa o recorte) — a empresa apareceria duas vezes.
        int novos = 0, atualizados = 0, processados = 0;
        foreach (var (basicoEmpresa, novo) in selecionados)
        {
            ct.ThrowIfCancellationRequested();
            var existente = await _db.Leads.FirstOrDefaultAsync(
                l => l.Origem == Lead.OrigemReceita && l.Cnpj != null && l.Cnpj.StartsWith(basicoEmpresa), ct);
            if (existente is null)
            {
                _db.Leads.Add(novo);
                novos++;
            }
            else if (existente.EditadoManualmente)
            {
                continue; // ajustado à mão na plataforma — o reimport não sobrescreve
            }
            else
            {
                existente.Cnpj = novo.Cnpj; // pode migrar de filial p/ matriz entre importações
                existente.RazaoSocial = novo.RazaoSocial;
                existente.Cnae = novo.Cnae;
                existente.Uf = novo.Uf;
                existente.Municipio = novo.Municipio;
                existente.CapitalSocial = novo.CapitalSocial;
                existente.Situacao = novo.Situacao;
                existente.PorteEstimado = novo.PorteEstimado;
                existente.Contato = novo.Contato;
                atualizados++;
            }

            if (++processados % 500 == 0)
                await _db.SaveChangesAsync(ct);
        }
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Importação Receita concluída: {Novos} novo(s), {Atu} atualizado(s)", novos, atualizados);
        return new ResultadoImportacaoReceita(linhasEstab, selecionados.Count, novos, atualizados, true);
    }

    private static Dictionary<string, string> CarregarMunicipios(string[] arquivos)
    {
        var dict = new Dictionary<string, string>();
        foreach (var arq in arquivos)
        {
            using var r = new StreamReader(arq, CsvReceita.Latin1);
            string? linha;
            while ((linha = r.ReadLine()) is not null)
            {
                var c = CsvReceita.ParseLinha(linha);
                if (c.Length >= 2) dict[c[0].Trim()] = c[1].Trim();
            }
        }
        return dict;
    }

    private static string? MontarContato(string[] c, string municipio, string uf)
    {
        var partes = new List<string>();

        var ddd1 = c[21].Trim(); var tel1 = c[22].Trim();
        if (tel1.Length > 0) partes.Add($"Tel: ({ddd1}) {tel1}");

        var email = c[27].Trim();
        if (email.Length > 0) partes.Add($"E-mail: {email}");

        var endereco = string.Join(" ", new[] { c[13], c[14], c[15], c[17] }
            .Select(x => x.Trim()).Where(x => x.Length > 0));
        var cep = c[18].Trim();
        var full = string.Join(", ", new[] { endereco, $"{municipio}/{uf}", cep }.Where(x => x.Trim().Length > 0));
        if (full.Trim().Length > 0) partes.Add($"End: {full}");

        return partes.Count > 0 ? string.Join(" | ", partes) : null;
    }
}
