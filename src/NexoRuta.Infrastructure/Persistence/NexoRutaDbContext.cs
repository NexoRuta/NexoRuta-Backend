using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class NexoRutaDbContext(DbContextOptions<NexoRutaDbContext> options) : IdentityUserContext<Usuario, Guid>(options)
{
    public DbSet<AccesoPlataforma> AccesosPlataforma => Set<AccesoPlataforma>();
    public DbSet<RolPlataforma> RolesPlataforma => Set<RolPlataforma>();
    public DbSet<RolPlataformaPermiso> RolesPlataformaPermisos => Set<RolPlataformaPermiso>();

    public DbSet<Operador> Operadores => Set<Operador>();
    public DbSet<Comercio> Comercios => Set<Comercio>();
    public DbSet<Usuario> Usuarios => Set<Usuario>();
    public DbSet<AccesoUsuario> AccesosUsuario => Set<AccesoUsuario>();
    public DbSet<SolicitudBajaOrganizacion> SolicitudesBajaOrganizacion => Set<SolicitudBajaOrganizacion>();
    public DbSet<SolicitudReactivacionOrganizacion> SolicitudesReactivacionOrganizacion => Set<SolicitudReactivacionOrganizacion>();
    public DbSet<CambioEstadoOrganizacion> CambiosEstadoOrganizacion => Set<CambioEstadoOrganizacion>();
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
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasAnnotation("NexoRuta:RlsVersion", 1);
        modelBuilder.Entity<RolPlataformaPermiso>(entity =>
        {
            entity.ToTable("RolesPlataformaPermisos");
            entity.HasKey(x => new { x.RolId, x.PermisoId });
            entity.HasOne<RolPlataforma>().WithMany().HasForeignKey(x => x.RolId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Permiso>().WithMany().HasForeignKey(x => x.PermisoId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<RolPlataforma>(entity =>
        {
            entity.ToTable("RolesPlataforma", t => t.HasCheckConstraint("CK_RolesPlataforma_Protegido", "\"EsProtegidoSistema\""));
            entity.HasKey(x => x.Id);
        });
        modelBuilder.Entity<AccesoPlataforma>(entity =>
        {
            entity.ToTable("AccesosPlataforma", t => t.HasCheckConstraint("CK_AccesosPlataforma_Inicial", "\"Id\" = 1"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasIndex(x => x.AccesoId).IsUnique();
            entity.HasIndex(x => x.UsuarioId).IsUnique();
            entity.Property(x => x.VersionSesion).IsConcurrencyToken();
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<RolPlataforma>().WithMany().HasForeignKey(x => x.RolId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Operador>(entity =>
        {
            entity.ToTable("Operadores");
            entity.Property<bool>("BootstrapCompletado").HasDefaultValue(false);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Nombre).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(EstadoOrganizacion.Activa).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
        });

        modelBuilder.Entity<Comercio>(entity =>
        {
            entity.ToTable("Comercios");
            entity.Property<bool>("BootstrapCompletado").HasDefaultValue(false);
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Nombre).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(EstadoOrganizacion.Activa).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
        });

        modelBuilder.Entity<SolicitudBajaOrganizacion>(entity =>
        {
            entity.ToTable("SolicitudesBajaOrganizacion", table =>
            {
                table.HasCheckConstraint("CK_SolicitudesBajaOrganizacion_Organizacion",
                    "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");
                table.HasCheckConstraint("CK_SolicitudesBajaOrganizacion_Resolucion",
                    "((\"Estado\" = 'Pendiente' AND \"ResueltaPorUsuarioId\" IS NULL AND \"ResueltaEnUtc\" IS NULL AND \"MotivoResolucion\" IS NULL) OR (\"Estado\" IN ('Aprobada', 'Rechazada') AND \"ResueltaPorUsuarioId\" IS NOT NULL AND \"ResueltaEnUtc\" IS NOT NULL AND \"MotivoResolucion\" IS NOT NULL))");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.TipoOrganizacion).HasConversion<string>().HasMaxLength(20)
                .HasComputedColumnSql("CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true)
                .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.OrganizacionId).HasComputedColumnSql("COALESCE(\"OperadorId\", \"ComercioId\")", stored: true)
                .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(EstadoSolicitudBaja.Pendiente).IsRequired();
            entity.Property(x => x.Motivo).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.MotivoResolucion).HasMaxLength(2000);
            entity.Property(x => x.Version).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
            entity.HasIndex(x => new { x.TipoOrganizacion, x.OrganizacionId }).IsUnique()
                .HasFilter("\"Estado\" = 'Pendiente'").HasDatabaseName("UX_SolicitudesBaja_Pendiente");
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.SolicitadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.ResueltaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Comercio>().WithMany().HasForeignKey(x => x.ComercioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SolicitudReactivacionOrganizacion>(entity =>
        {
            entity.ToTable("SolicitudesReactivacionOrganizacion", table =>
            {
                table.HasCheckConstraint("CK_SolicitudesReactivacionOrganizacion_Organizacion",
                    "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)");
                table.HasCheckConstraint("CK_SolicitudesReactivacionOrganizacion_Resolucion",
                    "((\"Estado\" = 'Pendiente' AND \"ResueltaPorUsuarioId\" IS NULL AND \"ResueltaEnUtc\" IS NULL AND \"MotivoResolucion\" IS NULL) OR (\"Estado\" IN ('Aprobada', 'Rechazada', 'AtendidaPorSoporte') AND \"ResueltaPorUsuarioId\" IS NOT NULL AND \"ResueltaEnUtc\" IS NOT NULL AND \"MotivoResolucion\" IS NOT NULL))");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.TipoOrganizacion).HasConversion<string>().HasMaxLength(20)
                .HasComputedColumnSql("CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true)
                .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.OrganizacionId).HasComputedColumnSql("COALESCE(\"OperadorId\", \"ComercioId\")", stored: true)
                .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(20)
                .HasDefaultValue(EstadoSolicitudReactivacion.Pendiente).IsRequired();
            entity.Property(x => x.Motivo).HasMaxLength(2000).IsRequired();
            entity.Property(x => x.MotivoResolucion).HasMaxLength(2000);
            entity.Property(x => x.Version).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
            entity.HasIndex(x => new { x.TipoOrganizacion, x.OrganizacionId }).IsUnique()
                .HasFilter("\"Estado\" = 'Pendiente'").HasDatabaseName("UX_SolicitudesReactivacion_Pendiente");
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.SolicitadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.ResueltaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Comercio>().WithMany().HasForeignKey(x => x.ComercioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CambioEstadoOrganizacion>(entity =>
        {
            entity.ToTable("CambiosEstadoOrganizacion", table =>
                table.HasCheckConstraint("CK_CambiosEstadoOrganizacion_Organizacion",
                    "(\"OperadorId\" IS NULL) <> (\"ComercioId\" IS NULL)"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.TipoOrganizacion).HasConversion<string>().HasMaxLength(20)
                .HasComputedColumnSql("CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true)
                .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.OrganizacionId).HasComputedColumnSql("COALESCE(\"OperadorId\", \"ComercioId\")", stored: true)
                .ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.EstadoAnterior).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.EstadoNuevo).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.Motivo).HasMaxLength(2000).IsRequired();
            entity.HasIndex(x => new { x.TipoOrganizacion, x.OrganizacionId, x.OcurridoEnUtc });
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.ActorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SolicitudBajaOrganizacion>().WithMany().HasForeignKey(x => x.SolicitudBajaId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Comercio>().WithMany().HasForeignKey(x => x.ComercioId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Usuario>(entity =>
        {
            // Keep the existing account table as the sole Identity user store so existing foreign keys and IDs remain intact.
            entity.ToTable("Usuarios");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PasswordHash)
                .HasMaxLength(1024);
            entity.Property(x => x.Email).HasMaxLength(254).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(0L).IsConcurrencyToken();
            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasIndex(x => x.NormalizedEmail).IsUnique();
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
            entity.Property(x => x.TipoOrganizacion).HasConversion<string>().HasMaxLength(20).HasComputedColumnSql("CASE WHEN \"OperadorId\" IS NOT NULL THEN 'Operador' ELSE 'Comercio' END", stored: true).ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.OrganizacionId).HasComputedColumnSql("COALESCE(\"OperadorId\", \"ComercioId\")", stored: true).ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.Property(x => x.EsPropietario).HasDefaultValue(false).ValueGeneratedNever().IsRequired();
            entity.Property(x => x.Activo)
                .HasDefaultValue(true)
                .IsRequired();
            entity.Property(x => x.VersionSesion)
                .HasDefaultValue(0L)
                .IsConcurrencyToken();
            entity.HasIndex(x => x.UsuarioId)
                .IsUnique();
            entity.HasIndex(x => new { x.TipoOrganizacion, x.OrganizacionId })
                .IsUnique()
                .HasFilter("\"EsPropietario\" = TRUE");

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
                .HasForeignKey("RolId", "TipoOrganizacion", "OrganizacionId")
                .HasPrincipalKey(x => new { x.Id, x.TipoOrganizacion, x.OrganizacionId })
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
            entity.ToTable("Roles", table => table.HasCheckConstraint("CK_Roles_Pertenencia",
                "((\"OperadorId\" IS NOT NULL AND \"ComercioId\" IS NULL AND \"TipoOrganizacion\" = 'Operador') OR (\"ComercioId\" IS NOT NULL AND \"OperadorId\" IS NULL AND \"TipoOrganizacion\" = 'Comercio'))"));
            entity.Property(x => x.EsProtegidoSistema).HasDefaultValue(false).IsRequired();
            entity.Property(x => x.OrganizacionId).HasComputedColumnSql("COALESCE(\"OperadorId\", \"ComercioId\")", stored: true).ValueGeneratedOnAdd().Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
            entity.HasAlternateKey(x => new { x.Id, x.TipoOrganizacion, x.OrganizacionId });
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Comercio>().WithMany().HasForeignKey(x => x.ComercioId).OnDelete(DeleteBehavior.Restrict);

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Codigo)
                .HasMaxLength(80)
                .IsRequired();

            entity.HasIndex(x => new { x.TipoOrganizacion, x.OrganizacionId, x.Codigo })
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
