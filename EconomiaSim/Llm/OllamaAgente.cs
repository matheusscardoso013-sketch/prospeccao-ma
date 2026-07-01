using System.Net.Http.Json;
using System.Text.Json;
using EconomiaSim.Model;

namespace EconomiaSim.Llm;

/// <summary>
/// Camada HÍBRIDA: usa um LLM local (Ollama) para dar uma leitura QUALITATIVA do
/// humor/decisão de um agente representativo de cada classe diante do cenário.
///
/// O núcleo numérico continua sendo a "verdade" da simulação; esta camada
/// enriquece com narrativa e pode, no futuro, realimentar parâmetros
/// (ex.: ajustar propensão a consumir conforme o "sentimento" reportado).
///
/// Opcional: se o Ollama não estiver rodando, a simulação segue sem ela.
/// </summary>
public class OllamaAgente
{
    private readonly HttpClient _http;
    private readonly string _modelo;

    public OllamaAgente(string modelo = "llama3.2", string baseUrl = "http://localhost:11434")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _modelo = modelo;
    }

    public async Task<bool> DisponivelAsync()
    {
        try { return (await _http.GetAsync("/api/tags")).IsSuccessStatusCode; }
        catch { return false; }
    }

    /// <summary>
    /// Pergunta ao LLM como uma família representativa da classe reage ao mês atual.
    /// Retorna um texto curto (1-2 frases) em português.
    /// </summary>
    public async Task<string> ReacaoDaClasseAsync(ClasseRenda classe, RegistroMes m)
    {
        var mc = m.PorClasse[classe];
        string prompt =
            $"Você é o chefe de uma família brasileira da classe {classe}. " +
            $"Contexto econômico atual: Selic {m.SelicAnual:P1} ao ano, inflação {m.InflacaoAnual:P1} ao ano, " +
            $"desemprego na sua classe {mc.Desemprego:P0}. " +
            "Em 1 a 2 frases, descreva como você se sente e o que vai fazer com seu dinheiro neste mês " +
            "(consumir, cortar gastos, pegar crédito ou poupar). Seja realista e direto.";

        try
        {
            var resp = await _http.PostAsJsonAsync("/api/generate", new
            {
                model = _modelo,
                prompt,
                stream = false
            });
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("response").GetString()?.Trim() ?? "(sem resposta)";
        }
        catch (Exception ex)
        {
            return $"(LLM indisponível: {ex.Message})";
        }
    }
}
