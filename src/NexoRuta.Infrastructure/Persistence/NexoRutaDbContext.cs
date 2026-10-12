using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class NexoRutaDbContext(DbContextOptions<NexoRutaDbContext> options) : DbContext(options)
{
    public DbSet<Operador> Operadores => Set<Operador>();
    public DbSet<Comercio> Comercios => Set<Comercio>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<AccesoUsuario> AccesosUsuario => Set<AccesoUsuario>();
    public DbSet<Destinatario> Destinatarios => Set<Destinatario>();
    public DbSet<Direccion> Direcciones => Set<Direccion>();
    public DbSet<Envio> Envios => Set<Envio>();
    public DbSet<Bulto> Bultos => Set<Bulto>();
    public DbSet<EnlaceSeguimiento> EnlacesSeguimiento => Set<EnlaceSeguimiento>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<EntregaSeguimiento> EntregasSeguimiento => Set<EntregaSeguimiento>();

    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Permiso> Permisos => Set<Permiso>();
    public DbSet<RolPermiso> RolesPermisos => Set<RolPermiso>();

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


        modelBuilder.Entity<AccesoUsuario>(entity =>
        {
            entity.ToTable("AccesosUsuario", table =>
            {
                table.HasCheckConstraint(
                    "CK_AccesosUsuario_Pertenencia",
                    "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");
            });

            entity.HasKey(x => x.Id);

            entity.HasIndex(x => x.UsuarioId)
                .IsUnique();

            entity.HasOne<Usuario>()
                .WithMany()
                .HasForeignKey(x => x.UsuarioId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Operador>()
                .WithMany()
                .HasForeignKey(x => x.OperadorId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Comercio>()
                .WithMany()
                .HasForeignKey(x => x.ComercioId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Rol>()
                .WithMany()
                .HasForeignKey(x => x.RolId)
                .IsRequired()
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
            entity.HasOne<Comercio>().WithMany().HasForeignKey(x => x.ComercioId).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");

            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProcesadorId);

            entity.Property(x => x.ReservaHastaUtc);

            entity.Property(x => x.Tipo)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(x => x.Destino)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(x => x.Estado)
                .HasConversion<string>()
                .HasMaxLength(32)
                .IsRequired();

            entity.Property(x => x.Payload)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(x => x.UltimoError)
                .HasMaxLength(2000);

            entity.HasIndex(x => new
            {
                x.Estado,
                x.ProximoIntentoEnUtc
            });

            entity.HasOne<Envio>()
                .WithMany()
                .HasForeignKey(x => new
                {
                    x.OperadorId,
                    x.EnvioId
                })
                .HasPrincipalKey(x => new
                {
                    x.OperadorId,
                    x.Id
                })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProcessedMessage>(entity =>
        {
            entity.ToTable("ProcessedMessages");

            entity.HasKey(x => new
            {
                x.MessageId,
                x.Consumer
            });

            entity.Property(x => x.Consumer)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(x => x.ProcessedAtUtc)
                .IsRequired();
        });

        modelBuilder.Entity<EntregaSeguimiento>(entity =>
        {
            entity.ToTable("EntregasSeguimiento");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.TokenHash)
                .HasMaxLength(64)
                .IsRequired();

            entity.HasIndex(x => x.TokenHash)
                .IsUnique();

            entity.Property(x => x.TokenCifrado)
                .HasColumnType("bytea")
                .IsRequired();

            entity.Property(x => x.Nonce)
                .HasColumnType("bytea")
                .IsRequired();

            entity.Property(x => x.Tag)
                .HasColumnType("bytea")
                .IsRequired();

            entity.HasOne<NexoRuta.Domain.Envios.Envio>()
                .WithMany()
                .HasForeignKey(x => new
                {
                    x.OperadorId,
                    x.EnvioId
                })
                .HasPrincipalKey(x => new
                {
                    x.OperadorId,
                    x.Id
                })
                .OnDelete(DeleteBehavior.Restrict);
        });


        modelBuilder.Entity<Rol>(entity =>
        {
            entity.ToTable("Roles");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Codigo)
                .HasMaxLength(80)
                .IsRequired();

            entity.HasIndex(x => x.Codigo)
                .IsUnique();

            entity.Property(x => x.Nombre)
                .HasMaxLength(160)
                .IsRequired();

            entity.Property(x => x.TipoOrganizacion)
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
        });



        modelBuilder.Entity<Permiso>(entity =>
        {
            entity.ToTable("Permisos");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Codigo)
                .HasMaxLength(80)
                .IsRequired();

            entity.HasIndex(x => x.Codigo)
                .IsUnique();

            entity.Property(x => x.Nombre)
                .HasMaxLength(160)
                .IsRequired();
        });


        modelBuilder.Entity<RolPermiso>(entity =>
        {
            entity.ToTable("RolesPermisos");

            entity.HasKey(x => new
            {
                x.RolId,
                x.PermisoId
            });

            entity.HasOne<Rol>()
                .WithMany()
                .HasForeignKey(x => x.RolId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<Permiso>()
                .WithMany()
                .HasForeignKey(x => x.PermisoId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.ApplyConfiguration(new EnlaceSeguimientoConfiguration());
    }
}
