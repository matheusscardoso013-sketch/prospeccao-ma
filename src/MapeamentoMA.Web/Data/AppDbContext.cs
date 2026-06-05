using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MapeamentoMA.Web.Models;

namespace MapeamentoMA.Web.Data;

public class AppDbContext : IdentityDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Tese> Teses => Set<Tese>();
    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<Gatilho> Gatilhos => Set<Gatilho>();
    public DbSet<Score> Scores => Set<Score>();
    public DbSet<PipelineItem> PipelineItems => Set<PipelineItem>();
    public DbSet<JobExecucao> JobExecucoes => Set<JobExecucao>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Empresa>(e =>
        {
            e.HasIndex(x => x.Cnpj).IsUnique();
            e.HasIndex(x => x.CnaePrincipal);
        });

        builder.Entity<PipelineItem>(e =>
        {
            e.HasIndex(x => x.Estagio);
            e.HasIndex(x => x.EmpresaId).IsUnique();
        });

        builder.Entity<Gatilho>(e =>
        {
            e.HasIndex(x => new { x.EmpresaId, x.DataEvento });
            e.HasIndex(x => x.HashEvento).IsUnique();
        });
    }
}
