namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Uma interação do time com um match (mini-CRM): anotação datada e assinada.
/// O histórico conta a história do negócio — e alimenta o aprendizado do matching.
/// </summary>
public class InteracaoMatch
{
    public int Id { get; set; }

    public int SinergiaId { get; set; }
    public SinergiaComprador? Sinergia { get; set; }

    /// <summary>Quem registrou (e-mail do usuário logado).</summary>
    public string Autor { get; set; } = string.Empty;

    public string Texto { get; set; } = string.Empty;

    public DateTime Em { get; set; } = DateTime.UtcNow;
}
