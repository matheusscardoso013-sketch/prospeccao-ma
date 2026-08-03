using System.Text.Json;
using ProspeccaoMA.Web.IA;

namespace ProspeccaoMA.Web.Ingestao;

/// <summary>
/// Lista os modelos que a chave enxerga na API do Gemini. Serve a uma decisão concreta: a
/// cota gratuita é de ~20 requisições/dia POR MODELO, então a capacidade diária da
/// plataforma é (nº de modelos na rotação × 20). Descobrir modelos novos é a forma mais
/// barata de aumentar a vazão — não custa nada e não depende de plano pago.
/// A chamada de listagem NÃO consome cota de geração. Uso:
///   dotnet run --project src/ProspeccaoMA.Web -- modelos
/// </summary>
public static class ComandoModelos
{
    public static async Task ExecutarAsync(IServiceProvider sp, IConfiguration cfg)
    {
        var opt = new GeminiOptions();
        cfg.GetSection("Gemini").Bind(opt);
        if (string.IsNullOrWhiteSpace(opt.ApiKey))
        {
            Console.WriteLine("Gemini:ApiKey não configurada.");
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") };
        var resp = await http.GetAsync($"v1beta/models?key={opt.ApiKey}&pageSize=200");
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"Falha ao listar modelos: HTTP {(int)resp.StatusCode}");
            return;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("models", out var arr)) { Console.WriteLine("Resposta sem 'models'."); return; }

        var emUso = opt.Modelos.Concat(opt.ModelosPrecisos).Select(m => m.Trim()).ToHashSet();
        var geradores = new List<string>();

        foreach (var m in arr.EnumerateArray())
        {
            var nome = m.GetProperty("name").GetString()?.Replace("models/", "") ?? "";
            var metodos = m.TryGetProperty("supportedGenerationMethods", out var sg)
                ? sg.EnumerateArray().Select(x => x.GetString()).ToList()
                : new List<string?>();
            if (!metodos.Contains("generateContent")) continue;
            if (nome.Contains("embedding") || nome.Contains("aqa")) continue;
            geradores.Add(nome);
        }

        Console.WriteLine($"=== Modelos com generateContent visíveis para esta chave: {geradores.Count} ===\n");
        Console.WriteLine("EM USO na rotação:");
        foreach (var n in geradores.Where(n => emUso.Contains(n)).OrderBy(n => n))
            Console.WriteLine($"  * {n}");

        Console.WriteLine("\nDISPONÍVEIS e FORA da rotação (candidatos a aumentar a cota):");
        foreach (var n in geradores.Where(n => !emUso.Contains(n)).OrderBy(n => n))
            Console.WriteLine($"    {n}");

        Console.WriteLine($"\nCapacidade hoje: {opt.Modelos.Length} modelo(s) x ~20/dia = ~{opt.Modelos.Length * 20} chamadas.");

        if (!Environment.GetCommandLineArgs().Any(a => a.Equals("--testar", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("\nUse --testar para validar os candidatos (gasta 1 chamada por modelo).");
            return;
        }

        // Só entra na rotação quem devolve JSON estrito: um modelo que responde em prosa
        // (caso do gemma, testado em julho) faz o parse falhar e queima a vaga na rotação.
        string[] candidatos =
        {
            "gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-3-flash-preview",
            "gemini-3.5-flash-lite", "gemini-3.6-flash", "gemini-3.1-flash-lite-preview"
        };

        Console.WriteLine("\n=== Teste de compatibilidade (JSON estrito) ===\n");
        var aprovados = new List<string>();
        foreach (var modelo in candidatos.Where(c => geradores.Contains(c) && !emUso.Contains(c)))
        {
            var corpo = new
            {
                contents = new[] { new { parts = new[] { new { text = "Responda apenas: {\"score\":42,\"racional\":\"teste\"}" } } } },
                generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
            };
            try
            {
                var r = await http.PostAsync($"v1beta/models/{modelo}:generateContent?key={opt.ApiKey}",
                    new StringContent(JsonSerializer.Serialize(corpo), System.Text.Encoding.UTF8, "application/json"));
                var texto = await r.Content.ReadAsStringAsync();
                if (!r.IsSuccessStatusCode)
                {
                    Console.WriteLine($"  X  {modelo,-32} HTTP {(int)r.StatusCode}");
                    continue;
                }
                using var d = JsonDocument.Parse(texto);
                var saida = d.RootElement.GetProperty("candidates")[0]
                    .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                var recortado = GeminiClassificador.RecortarJson(saida.Trim());
                using var _ = JsonDocument.Parse(recortado); // se não for JSON válido, estoura
                Console.WriteLine($"  OK {modelo,-32} devolveu JSON válido");
                aprovados.Add(modelo);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  X  {modelo,-32} {ex.GetType().Name}");
            }
        }

        if (aprovados.Count > 0)
        {
            var nova = opt.Modelos.Concat(aprovados).ToArray();
            Console.WriteLine($"\nRotação sugerida ({nova.Length} modelos = ~{nova.Length * 20} chamadas/dia):");
            Console.WriteLine("  \"Modelos\": [" + string.Join(", ", nova.Select(m => $"\"{m}\"")) + "]");
        }
    }
}
