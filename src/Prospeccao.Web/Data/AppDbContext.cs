using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Prospeccao.Web.Models;

namespace Prospeccao.Web.Data;

/// <summary>
/// Contexto EF Core da plataforma. Herda do Identity (tabelas de usuário/papel)
/// e adiciona as entidades de prospecção. Provider: Npgsql (PostgreSQL no Neon).
/// </summary>
public class AppDbContext : IdentityDbContext<IdentityUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ConfiguracaoProspeccao> ConfiguracoesProspeccao => Set<ConfiguracaoProspeccao>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadScore> LeadScores => Set<LeadScore>();
    public DbSet<ExecucaoJob> ExecucoesJob => Set<ExecucaoJob>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // CNPJ é a chave natural de deduplicação — único.
        builder.Entity<Lead>()
            .HasIndex(l => l.Cnpj)
            .IsUnique();

        // CNAE indexado para o filtro de prospecção por setor.
        builder.Entity<Lead>()
            .HasIndex(l => l.Cnae);

        // Precisão monetária explícita (evita aviso do Npgsql sobre decimal).
        builder.Entity<Lead>()
            .Property(l => l.CapitalSocial)
            .HasPrecision(18, 2);

        builder.Entity<ConfiguracaoProspeccao>(e =>
        {
            e.Property(c => c.CapitalMin).HasPrecision(18, 2);
            e.Property(c => c.CapitalMax).HasPrecision(18, 2);
        });

        // LeadScore: N–1 para Lead e para Configuração. Não apagar histórico em cascata
        // ao remover uma configuração.
        builder.Entity<LeadScore>(e =>
        {
            e.HasOne(s => s.Lead)
                .WithMany(l => l.Scores)
                .HasForeignKey(s => s.LeadId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.Configuracao)
                .WithMany(c => c.Scores)
                .HasForeignKey(s => s.ConfiguracaoId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
