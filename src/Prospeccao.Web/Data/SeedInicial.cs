using Microsoft.AspNetCore.Identity;

namespace Prospeccao.Web.Data;

/// <summary>Cria os papéis padrão (Socio, Analista) no startup, se ainda não existirem.</summary>
public static class SeedInicial
{
    public static readonly string[] Papeis = { "Socio", "Analista" };

    public static async Task SeedAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var papel in Papeis)
        {
            if (!await roleManager.RoleExistsAsync(papel))
                await roleManager.CreateAsync(new IdentityRole(papel));
        }
    }
}
