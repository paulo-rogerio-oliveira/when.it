using Contabilidade.Sandbox.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Contabilidade.Sandbox.Api.Data;

public class ContabilidadeContext : DbContext
{
    public ContabilidadeContext(DbContextOptions<ContabilidadeContext> options) : base(options) { }

    public DbSet<Empresa> Empresas => Set<Empresa>();
    public DbSet<DadosGrandePorte> DadosGrandePorte => Set<DadosGrandePorte>();
    public DbSet<DadosMicroEmpresa> DadosMicroEmpresa => Set<DadosMicroEmpresa>();
    public DbSet<PlanoConta> PlanoContas => Set<PlanoConta>();
    public DbSet<Lancamento> Lancamentos => Set<Lancamento>();
    public DbSet<LancamentoLinha> LancamentoLinhas => Set<LancamentoLinha>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Empresa>(e =>
        {
            e.ToTable("empresas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Cnpj).HasMaxLength(20).IsRequired();
            e.Property(x => x.RazaoSocial).HasMaxLength(200).IsRequired();
            e.Property(x => x.NomeFantasia).HasMaxLength(200);
            e.Property(x => x.Porte).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => x.Cnpj).IsUnique();

            e.HasOne(x => x.DadosGrandePorte)
             .WithOne()
             .HasForeignKey<DadosGrandePorte>(d => d.EmpresaId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.DadosMicroEmpresa)
             .WithOne()
             .HasForeignKey<DadosMicroEmpresa>(d => d.EmpresaId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DadosGrandePorte>(e =>
        {
            e.ToTable("dados_grande_porte");
            e.HasKey(x => x.EmpresaId);
            e.Property(x => x.FaturamentoAnualMilhoes).HasColumnType("decimal(18,2)");
        });

        b.Entity<DadosMicroEmpresa>(e =>
        {
            e.ToTable("dados_microempresa");
            e.HasKey(x => x.EmpresaId);
            e.Property(x => x.RegimeTributario).HasMaxLength(40).IsRequired();
            e.Property(x => x.CnaePrincipal).HasMaxLength(20).IsRequired();
        });

        b.Entity<PlanoConta>(e =>
        {
            e.ToTable("plano_contas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Codigo).HasMaxLength(40).IsRequired();
            e.Property(x => x.Nome).HasMaxLength(200).IsRequired();
            // Enums como string deixam o SQL capturado mais legível pra inferência do DbSense.
            e.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Natureza).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.EmpresaId, x.Codigo }).IsUnique();
            e.HasIndex(x => x.ContaPaiId);
        });

        b.Entity<Lancamento>(e =>
        {
            e.ToTable("lancamentos");
            e.HasKey(x => x.Id);
            e.Property(x => x.Historico).HasMaxLength(500).IsRequired();
            e.Property(x => x.ValorTotal).HasColumnType("decimal(18,2)");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.EmpresaId, x.Numero }).IsUnique();
            e.HasIndex(x => x.DataCompetencia);
            e.HasMany(x => x.Linhas)
             .WithOne()
             .HasForeignKey(x => x.LancamentoId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<LancamentoLinha>(e =>
        {
            e.ToTable("lancamento_linhas");
            e.HasKey(x => x.Id);
            e.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Valor).HasColumnType("decimal(18,2)");
            e.Property(x => x.Historico).HasMaxLength(500);
            e.HasIndex(x => x.LancamentoId);
            e.HasIndex(x => x.ContaId);
        });
    }
}
