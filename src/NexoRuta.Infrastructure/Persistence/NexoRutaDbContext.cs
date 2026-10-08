using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class NexoRutaDbContext(DbContextOptions<NexoRutaDbContext> options) : DbContext(options)
{
    public DbSet<Operador> Operadores => Set<Operador>();
    public DbSet<Comercio> Comercios => Set<Comercio>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<OperadorComercio> OperadoresComercios => Set<OperadorComercio>();
    public DbSet<AccesoUsuario> AccesosUsuario => Set<AccesoUsuario>();
    public DbSet<Destinatario> Destinatarios => Set<Destinatario>();
    public DbSet<Direccion> Direcciones => Set<Direccion>();
    public DbSet<Envio> Envios => Set<Envio>();
    public DbSet<Bulto> Bultos => Set<Bulto>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Operador>(entity =>
        {
            entity.ToTable("Operadores");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Nombre).HasMaxLength(160).IsRequired();
        });

        modelBuilder.Entity<Comercio>(entity =>
        {
            entity.ToTable("Comercios");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Nombre).HasMaxLength(160).IsRequired();
        });

        modelBuilder.Entity<Usuario>(entity =>
        {
            entity.ToTable("Usuarios");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Email).HasMaxLength(254).IsRequired();
            entity.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<OperadorComercio>(entity =>
        {
            entity.ToTable("OperadoresComercios");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.HasIndex(x => new { x.OperadorId, x.ComercioId }).IsUnique();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Comercio>().WithMany().HasForeignKey(x => x.ComercioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccesoUsuario>(entity =>
        {
            entity.ToTable("AccesosUsuario");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UsuarioId, x.OperadorComercioId }).IsUnique();
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OperadorComercio>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.OperadorComercioId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Destinatario>(entity =>
        {
            entity.ToTable("Destinatarios");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Nombre).HasMaxLength(160).IsRequired();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Direccion>(entity =>
        {
            entity.ToTable("Direcciones");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Descripcion).HasMaxLength(240).IsRequired();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Envio>(entity =>
        {
            entity.ToTable("Envios");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OperadorComercio>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.OperadorComercioId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.CreadoPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Destinatario>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.DestinatarioId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Direccion>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.DireccionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bulto>(entity =>
        {
            entity.ToTable("Bultos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Codigo).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PesoGramos).HasPrecision(12, 2);
            entity.Property(x => x.LargoCentimetros).HasPrecision(12, 2);
            entity.Property(x => x.AnchoCentimetros).HasPrecision(12, 2);
            entity.Property(x => x.AltoCentimetros).HasPrecision(12, 2);
            entity.HasOne<Envio>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.EnvioId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
