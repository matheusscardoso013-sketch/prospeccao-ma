using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Classificacao;
using MapeamentoMA.Web.Data;
using MapeamentoMA.Web.Ingestao;
using MapeamentoMA.Web.Jobs;
using MapeamentoMA.Web.Scoring;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    if (builder.Environment.IsDevelopment())
        options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=mapeamento.db");
    else
        options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"));
});

builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = false;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders()
.AddDefaultUI();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Identity/Account/Login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
});

// Scoring
builder.Services.Configure<ScoringConfig>(builder.Configuration.GetSection("Scoring"));
builder.Services.AddScoped<MotorScoring>();

// Classificação IA
builder.Services.AddHttpClient<IClassificadorIA, OllamaClassificador>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"));

// Enriquecimento de CNPJ
builder.Services.AddHttpClient<ConectorBrasilApi>(c =>
    c.BaseAddress = new Uri("https://brasilapi.com.br"));

// Conectores de ingestão (registrados pelo tipo concreto + alias pela interface)
builder.Services.AddHttpClient<ConectorRssNoticias>();
builder.Services.AddHttpClient<ConectorCvm>();
builder.Services.AddTransient<IConectorFonte, ConectorRssNoticias>();
builder.Services.AddTransient<IConectorFonte, ConectorCvm>();

// Job de ingestão
builder.Services.AddSingleton<CicloIngestaoService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CicloIngestaoService>());

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

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
    pattern: "{controller=Pipeline}/{action=Index}/{id?}");
app.MapRazorPages();

await SeedAsync(app);

app.Run();

static async Task SeedAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

    foreach (var papel in new[] { "Socio", "Analista" })
    {
        if (!await roleManager.RoleExistsAsync(papel))
            await roleManager.CreateAsync(new IdentityRole(papel));
    }
}
