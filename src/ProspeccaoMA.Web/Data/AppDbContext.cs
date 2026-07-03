using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ProspeccaoMA.Web.Models;

namespace ProspeccaoMA.Web.Data;

/// <summary>
/// Contexto EF Core (provider Npgsql/PostgreSQL — Neon). Herda do IdentityDbContext
/// para a tabela de Usuarios/login. Demais tabelas seguem a spec seção 3.
/// Implementa IDataProtectionKeyContext para persistir as chaves de criptografia no banco
/// (assim os cookies de login sobrevivem aos redeploys no Render — disco efêmero).
/// </summary>
public class AppDbContext : IdentityDbContext<Usuario>, IDataProtectionKeyContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ConfiguracaoProspeccao> Configuracoes => Set<ConfiguracaoProspeccao>();
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<LeadScore> LeadScores => Set<LeadScore>();
    public DbSet<ExecucaoJob> ExecucoesJob => Set<ExecucaoJob>();
    public DbSet<Comprador> Compradores => Set<Comprador>();
    public DbSet<SinergiaComprador> SinergiasComprador => Set<SinergiaComprador>();
    public DbSet<InteracaoMatch> InteracoesMatch => Set<InteracaoMatch>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // CNPJ único quando presente — base da deduplicação idempotente do job diário.
        // (No Postgres, NULLs não colidem no índice único — alvos curados sem CNPJ coexistem.)
        builder.Entity<Lead>()
            .HasIndex(l => l.Cnpj)
            .IsUnique();

        builder.Entity<Lead>()
            .Property(l => l.Origem)
            .HasDefaultValue(Lead.OrigemReceita);

        builder.Entity<Lead>()
            .Property(l => l.CapitalSocial)
            .HasPrecision(18, 2);

        builder.Entity<ConfiguracaoProspeccao>()
            .Property(c => c.CapitalMin)
            .HasPrecision(18, 2);
        builder.Entity<ConfiguracaoProspeccao>()
            .Property(c => c.CapitalMax)
            .HasPrecision(18, 2);

        // Um lead é pontuado no máximo uma vez por configuração (idempotência do score).
        builder.Entity<LeadScore>()
            .HasIndex(s => new { s.LeadId, s.ConfiguracaoId })
            .IsUnique();

        builder.Entity<LeadScore>()
            .HasOne(s => s.Lead)
            .WithMany(l => l.Scores)
            .HasForeignKey(s => s.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<LeadScore>()
            .HasOne(s => s.Configuracao)
            .WithMany()
            .HasForeignKey(s => s.ConfiguracaoId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ConfiguracaoProspeccao>()
            .HasOne(c => c.Usuario)
            .WithMany(u => u.Configuracoes)
            .HasForeignKey(c => c.UsuarioId)
            .OnDelete(DeleteBehavior.Cascade);

        // Nome único do comprador — deduplica a importação da planilha.
        builder.Entity<Comprador>()
            .HasIndex(c => c.Nome)
            .IsUnique();

        builder.Entity<Comprador>().Property(c => c.FaturamentoMinAlvo).HasPrecision(18, 2);
        builder.Entity<Comprador>().Property(c => c.FaturamentoMaxAlvo).HasPrecision(18, 2);
        builder.Entity<Comprador>().Property(c => c.MargemEbitdaMinima).HasPrecision(5, 2);

        // Uma sinergia por par (lead, comprador) — idempotência do matching.
        builder.Entity<SinergiaComprador>()
            .HasIndex(s => new { s.LeadId, s.CompradorId })
            .IsUnique();

        builder.Entity<SinergiaComprador>()
            .HasOne(s => s.Lead)
            .WithMany()
            .HasForeignKey(s => s.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<SinergiaComprador>()
            .HasOne(s => s.Comprador)
            .WithMany(c => c.Sinergias)
            .HasForeignKey(s => s.CompradorId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<InteracaoMatch>()
            .HasOne(i => i.Sinergia)
            .WithMany(s => s.Interacoes)
            .HasForeignKey(i => i.SinergiaId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
