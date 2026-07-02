using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Data;
using ProspeccaoMA.Web.IA;
using ProspeccaoMA.Web.Ingestao;
using ProspeccaoMA.Web.Jobs;
using ProspeccaoMA.Web.Matching;
using ProspeccaoMA.Web.Models;

// Cultura pt-BR em todo o app (o container do Render roda em cultura neutra, que
// renderiza moeda como "¤" — aqui garantimos R$ e datas brasileiras).
var ptBr = new System.Globalization.CultureInfo("pt-BR");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = ptBr;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = ptBr;

var builder = WebApplication.CreateBuilder(args);

// Hospedagem (Render/Railway): escutar a porta informada pela plataforma via env PORT.
var portaHospedagem = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(portaHospedagem))
    builder.WebHost.UseUrls($"http://0.0.0.0:{portaHospedagem}");

// Connection string do Neon (Postgres). Lida de config/env/user-secrets, NUNCA do código.
// Aceita tanto a URI postgresql://... quanto o formato key=value.
var connNeon = NeonConnectionString.Normalizar(
    builder.Configuration.GetConnectionString("Neon")
    ?? builder.Configuration["Neon:ConnectionString"]);

if (string.IsNullOrWhiteSpace(connNeon))
{
    // Permite build e geração de migrations sem o Neon ainda conectado.
    // Em runtime real, defina ConnectionStrings:Neon (user-secrets/env) com a string do painel.
    connNeon = "Host=localhost;Database=prospeccao_dev;Username=postgres;Password=postgres";
}

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseNpgsql(connNeon));

// Persiste as chaves de Data Protection no banco (Neon) para os cookies de login
// sobreviverem aos redeploys do Render (disco efêmero recria as chaves a cada deploy).
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<AppDbContext>()
    .SetApplicationName("ProspeccaoMA");

builder.Services
    .AddIdentity<Usuario, IdentityRole>(opt =>
    {
        opt.SignIn.RequireConfirmedAccount = false;
        opt.Password.RequiredLength = 6;
        opt.Password.RequireNonAlphanumeric = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(opt =>
{
    opt.LoginPath = "/Conta/Login";
    opt.AccessDeniedPath = "/Conta/Login";
});

// Ingestão de dados reais.
builder.Services.AddHttpClient<IConectorBrasilApi, ConectorBrasilApi>(c =>
{
    c.BaseAddress = new Uri("https://brasilapi.com.br/");
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<IImportadorCnpj, ImportadorCnpj>();
builder.Services.AddScoped<IImportadorReceita, ImportadorReceita>();
builder.Services.AddScoped<IImportadorCompradores, ImportadorCompradores>();
builder.Services.AddScoped<IImportadorAlvosValore, ImportadorAlvosValore>();

// Qualificação por IA (Gemini free tier). Abstração IClassificadorIA — trocável por config.
builder.Services.AddHttpClient<IClassificadorIA, GeminiClassificador>(c =>
{
    c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(40);
});

// Motor de sinergia alvo × comprador (buy-side).
builder.Services.AddScoped<IMotorSinergia, MotorSinergia>();

// Resumo diário por e-mail (pós-rotina das 12h). Configurar seção Email via env vars.
builder.Services.AddScoped<ProspeccaoMA.Web.Notificacoes.INotificadorEmail, ProspeccaoMA.Web.Notificacoes.NotificadorEmail>();

// Rotina de prospecção (compartilhada) + agendador diário às 12h.
builder.Services.AddScoped<RotinaProspeccao>();
builder.Services.AddHostedService<JobProspeccaoService>();

builder.Services.AddControllersWithViews();

var app = builder.Build();

// Comando de console para importar o recorte da Receita (fora do fluxo web).
if (args.Length > 0 && string.Equals(args[0], "importar-receita", StringComparison.OrdinalIgnoreCase))
{
    await ComandoImportarReceita.ExecutarAsync(app.Services, args);
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "importar-compradores", StringComparison.OrdinalIgnoreCase))
{
    using var escopoC = app.Services.CreateScope();
    var imp = escopoC.ServiceProvider.GetRequiredService<IImportadorCompradores>();
    var rc = await imp.ImportarAsync(args[1]);
    Console.WriteLine($"Compradores: {rc.Novos} novo(s), {rc.Atualizados} atualizado(s) de {rc.LinhasBuySide} linha(s) buy-side.");
    return;
}

// Preenche teses de compradores a partir de um JSON {nome: tese} (pesquisa pública).
// SÓ preenche quem está sem tese (não sobrescreve teses vindas de reunião).
if (args.Length >= 2 && string.Equals(args[0], "importar-teses", StringComparison.OrdinalIgnoreCase))
{
    using var escopoT = app.Services.CreateScope();
    var dbT = escopoT.ServiceProvider.GetRequiredService<AppDbContext>();
    var json = await File.ReadAllTextAsync(args[1]);
    var teses = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
    int aplicadas = 0, puladas = 0, naoEncontrados = 0;
    foreach (var (nome, tese) in teses)
    {
        var c = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(dbT.Compradores, x => x.Nome == nome);
        if (c is null) { Console.WriteLine($"NÃO ENCONTRADO: {nome}"); naoEncontrados++; continue; }
        if (c.Tese.Trim().Length >= 20) { puladas++; continue; } // já tem tese real — não mexe
        c.Tese = "[Pesquisa pública — validar] " + tese.Trim();
        aplicadas++;
    }
    await dbT.SaveChangesAsync();
    Console.WriteLine($"Teses: {aplicadas} aplicada(s), {puladas} pulada(s) (já tinham tese), {naoEncontrados} não encontrado(s).");
    return;
}

if (args.Length >= 2 && string.Equals(args[0], "importar-alvos", StringComparison.OrdinalIgnoreCase))
{
    using var escopoA = app.Services.CreateScope();
    var impA = escopoA.ServiceProvider.GetRequiredService<IImportadorAlvosValore>();
    var ra = await impA.ImportarAsync(args[1]);
    Console.WriteLine($"Alvos Valore: {ra.Novos} novo(s), {ra.Atualizados} atualizado(s) de {ra.LinhasSellSide} linha(s) sell-side.");
    return;
}

// Onda "dado rico": IA extrai critérios estruturados das teses (estruturar-teses) e
// resume perfis a partir do site oficial (enriquecer-perfis). Ver ComandoDadoRico.
if (args.Length > 0 && string.Equals(args[0], "estruturar-teses", StringComparison.OrdinalIgnoreCase))
{
    await ComandoDadoRico.EstruturarTesesAsync(app.Services, args);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "enriquecer-perfis", StringComparison.OrdinalIgnoreCase))
{
    await ComandoDadoRico.EnriquecerPerfisAsync(app.Services, args);
    return;
}

if (args.Length > 0 && string.Equals(args[0], "gerar-embeddings", StringComparison.OrdinalIgnoreCase))
{
    await ComandoDadoRico.GerarEmbeddingsAsync(app.Services, args);
    return;
}

// Exporta o pool completo de empresas da Receita que está na plataforma para um .xlsx
// (entregável para o time — o dado bruto do governo tem dezenas de GB; este é o recorte útil).
if (args.Length >= 2 && string.Equals(args[0], "exportar-pool", StringComparison.OrdinalIgnoreCase))
{
    using var escopoX = app.Services.CreateScope();
    var dbX = escopoX.ServiceProvider.GetRequiredService<AppDbContext>();
    var leadsX = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
        dbX.Leads.Where(l => l.Origem == ProspeccaoMA.Web.Models.Lead.OrigemReceita)
            .OrderByDescending(l => l.CapitalSocial));

    using var wbX = new ClosedXML.Excel.XLWorkbook();
    var wsX = wbX.AddWorksheet("Empresas Receita");
    string[] cabX = { "Razão social", "CNPJ", "CNAE", "Atividade (oficial)", "Município", "UF",
        "Capital social", "Porte (estimado)", "Situação", "Contato", "Importado em" };
    for (var i = 0; i < cabX.Length; i++) wsX.Cell(1, i + 1).Value = cabX[i];
    wsX.Row(1).Style.Font.Bold = true;

    var linhaX = 2;
    foreach (var l in leadsX)
    {
        wsX.Cell(linhaX, 1).Value = l.RazaoSocial;
        wsX.Cell(linhaX, 2).Value = ProspeccaoMA.Web.Ingestao.CnpjUtil.Formatar(l.Cnpj);
        wsX.Cell(linhaX, 3).Value = l.Cnae;
        wsX.Cell(linhaX, 4).Value = ProspeccaoMA.Web.Util.CnaeCatalogo.Descricao(l.Cnae) ?? "";
        wsX.Cell(linhaX, 5).Value = l.Municipio;
        wsX.Cell(linhaX, 6).Value = l.Uf;
        wsX.Cell(linhaX, 7).Value = l.CapitalSocial;
        wsX.Cell(linhaX, 7).Style.NumberFormat.Format = "#,##0";
        wsX.Cell(linhaX, 8).Value = l.PorteEstimado;
        wsX.Cell(linhaX, 9).Value = l.Situacao;
        wsX.Cell(linhaX, 10).Value = l.Contato ?? "";
        wsX.Cell(linhaX, 11).Value = l.CriadoEm.ToString("dd/MM/yyyy");
        linhaX++;
    }
    // Larguras fixas (AdjustToContents em 5k linhas é lento e desnecessário aqui).
    double[] largX = { 52, 20, 10, 48, 24, 6, 16, 18, 10, 60, 12 };
    for (var i = 0; i < largX.Length; i++) wsX.Column(i + 1).Width = largX[i];

    wbX.SaveAs(args[1]);
    Console.WriteLine($"Exportado: {leadsX.Count} empresa(s) da Receita para {args[1]}");
    return;
}

// Atrás do proxy da hospedagem (Render/Railway): repassa esquema/host reais
// para o HTTPS e o cookie de login funcionarem corretamente.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    // Em produção o TLS é encerrado no proxy da plataforma; redirect só em dev.
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Aplica migrations e cria o usuário inicial. Resiliente: se o Neon ainda não estiver
// configurado/acessível, registra o aviso em vez de impedir o boot.
try
{
    await DbInicializador.InicializarAsync(app.Services);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Banco não inicializado (configure ConnectionStrings:Neon). App segue de pé para diagnóstico.");
}

app.Run();
