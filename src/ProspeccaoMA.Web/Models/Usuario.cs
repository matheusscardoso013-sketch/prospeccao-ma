using Microsoft.AspNetCore.Identity;

namespace ProspeccaoMA.Web.Models;

/// <summary>
/// Usuário da plataforma. Estende o Identity (Email/SenhaHash já vêm do IdentityUser:
/// Email e PasswordHash). Mantemos apenas o campo extra CriadoEm da spec (seção 3).
/// </summary>
public class Usuario : IdentityUser
{
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;

    public List<ConfiguracaoProspeccao> Configuracoes { get; set; } = new();
}
