using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Data;

/// <summary>
/// Aplica migrations no startup e cria um usuário inicial (a partir de config/secrets)
/// para permitir o primeiro login. Não cria nenhum dado de empresa — leads só entram
/// via importação real.
/// </summary>
public static class DbInicializador
{
    public static async Task InicializarAsync(IServiceProvider sp)
    {
        using var escopo = sp.CreateScope();
        var prov = escopo.ServiceProvider;
        var log = prov.GetRequiredService<ILogger<Program>>();

        var db = prov.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        var userManager = prov.GetRequiredService<UserManager<Usuario>>();
        var cfg = prov.GetRequiredService<IConfiguration>();

        var email = cfg["Admin:Email"] ?? "admin@prospeccao.local";
        var senha = cfg["Admin:Senha"] ?? "Prospeccao@123";

        if (await userManager.FindByEmailAsync(email) is null)
        {
            var usuario = new Usuario { UserName = email, Email = email, EmailConfirmed = true };
            var r = await userManager.CreateAsync(usuario, senha);
            if (r.Succeeded)
                log.LogInformation("Usuário inicial criado: {Email}", email);
            else
                log.LogWarning("Falha ao criar usuário inicial: {Erros}", string.Join("; ", r.Errors.Select(e => e.Description)));
        }
    }
}
