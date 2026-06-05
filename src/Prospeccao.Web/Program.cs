using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Data;
using Prospeccao.Web.IA;
using Prospeccao.Web.Ingestao;
using Prospeccao.Web.Jobs;

var builder = WebApplication.CreateBuilder(args);

// Seleção de banco:
//  - Se houver uma connection string REAL do Neon (user-secrets / variável de ambiente
//    ConnectionStrings__Neon), usa PostgreSQL no Neon (alvo de produção da spec).
//  - Caso contrário, cai para SQLite local (arquivo prospeccao.db) — zero configuração,
//    para rodar na máquina do dev sem conta na nuvem. A connection string NUNCA fica no código.
var neon = builder.Configuration.GetConnectionString("Neon");
var usarPostgres = !string.IsNullOrWhiteSpace(neon)
    && (neon.Contains("://") || neon.Contains("Host=", StringComparison.OrdinalIgnoreCase));

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (usarPostgres)
        options.UseNpgsql(NeonConnectionString.Normalizar(neon!));
    else
        options.UseSqlite(builder.Configuration.GetConnectionString("Sqlite")
            ?? "Data Source=prospeccao.db");
});

builder.Services.AddDefaultIdentity<IdentityUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = false;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<AppDbContext>();

// Cookies de sessão seguros (HttpOnly, Secure, SameSite).
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// --- Fase 2: ingestão (BrasilAPI), IA (Ollama) e job de prospecção ---
// Enriquecimento de CNPJ via BrasilAPI (dados reais da Receita).
builder.Services.AddHttpClient<ConectorBrasilApi>(c =>
{
    c.BaseAddress = new Uri("https://brasilapi.com.br");
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddScoped<ImportadorCnpj>();

// Qualificação via Ollama local (grátis). Timeout maior: modelo em CPU é mais lento.
builder.Services.AddHttpClient<IClassificadorIA, OllamaClassificador>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
    c.Timeout = TimeSpan.FromMinutes(3);
});

// Rotina (usada pelo job e pelo botão "Rodar agora") + agendamento diário às 12h.
builder.Services.AddScoped<RotinaProspeccao>();
builder.Services.AddHostedService<JobProspeccaoService>();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages(); // UI default do Identity (login/registro)

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

// Cria/atualiza o schema no startup e semeia os papéis Socio/Analista.
//  - Postgres (Neon): aplica as migrations versionadas.
//  - SQLite (dev local): cria o schema direto do modelo (sem migrations, que são
//    específicas do Npgsql).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsNpgsql())
        db.Database.Migrate();
    else
        db.Database.EnsureCreated();

    await SeedInicial.SeedAsync(scope.ServiceProvider);
}

app.Run();
