using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Administracion.Autenticacion;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class NexoRutaDbContext(DbContextOptions<NexoRutaDbContext> options) : IdentityUserContext<Usuario, Guid>(options)
{
    public DbSet<NexoRuta.Domain.Reparto.Repartidor> Repartidores => Set<NexoRuta.Domain.Reparto.Repartidor>();
    public DbSet<NexoRuta.Domain.Reparto.ConfiguracionReparto> ConfiguracionesReparto => Set<NexoRuta.Domain.Reparto.ConfiguracionReparto>();
    public DbSet<NexoRuta.Domain.Reparto.Vehiculo> Vehiculos => Set<NexoRuta.Domain.Reparto.Vehiculo>();
    public DbSet<NexoRuta.Domain.Reparto.HojaRuta> HojasRuta => Set<NexoRuta.Domain.Reparto.HojaRuta>();
    public DbSet<NexoRuta.Domain.Reparto.ParadaRuta> ParadasRuta => Set<NexoRuta.Domain.Reparto.ParadaRuta>();
    public DbSet<NexoRuta.Infrastructure.Reparto.OperacionJornada> OperacionesJornada => Set<NexoRuta.Infrastructure.Reparto.OperacionJornada>();
    public DbSet<NexoRuta.Infrastructure.Reparto.EvidenciaReparto> EvidenciasReparto => Set<NexoRuta.Infrastructure.Reparto.EvidenciaReparto>();
    public DbSet<NexoRuta.Infrastructure.Reparto.PruebaEntrega> PruebasEntrega => Set<NexoRuta.Infrastructure.Reparto.PruebaEntrega>();
    public DbSet<NexoRuta.Domain.Notificaciones.AvisoReparto> AvisosReparto => Set<NexoRuta.Domain.Notificaciones.AvisoReparto>();
    public DbSet<NexoRuta.Infrastructure.Notificaciones.InstalacionMovil> InstalacionesMoviles => Set<NexoRuta.Infrastructure.Notificaciones.InstalacionMovil>();
    public DbSet<NexoRuta.Infrastructure.Notificaciones.EntregaPush> EntregasPush => Set<NexoRuta.Infrastructure.Notificaciones.EntregaPush>();
    public DbSet<SesionMovil> SesionesMoviles => Set<SesionMovil>();
    public DbSet<TokenRenovacionMovil> TokensRenovacionMoviles => Set<TokenRenovacionMovil>();
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
    public DbSet<NexoRuta.Infrastructure.Envios.CancelacionEnvio> CancelacionesEnvio => Set<NexoRuta.Infrastructure.Envios.CancelacionEnvio>();
    public DbSet<Bulto> Bultos => Set<Bulto>();
    public DbSet<EnlaceSeguimiento> EnlacesSeguimiento => Set<EnlaceSeguimiento>();
    public DbSet<CodigoCancelacionPublica> CodigosCancelacionPublica => Set<CodigoCancelacionPublica>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<EntregaSeguimiento> EntregasSeguimiento => Set<EntregaSeguimiento>();

    public DbSet<ZonaCobertura> ZonasCobertura => Set<ZonaCobertura>();
    public DbSet<FranjaEntrega> FranjasEntrega => Set<FranjaEntrega>();
    public DbSet<ZonaCoberturaRevision> ZonasCoberturaRevisiones => Set<ZonaCoberturaRevision>();
    public DbSet<DepartamentoReferencia> DepartamentosReferencia => Set<DepartamentoReferencia>();
    public DbSet<TarifarioOperador> TarifariosOperador => Set<TarifarioOperador>();
    public DbSet<VersionTarifario> VersionesTarifario => Set<VersionTarifario>();
    public DbSet<TarifaZonaModalidad> TarifasZonaModalidad => Set<TarifaZonaModalidad>();
    public DbSet<AjusteTarifario> AjustesTarifario => Set<AjusteTarifario>();
    public DbSet<ReglasOperativasOperador> ReglasOperativasOperadores => Set<ReglasOperativasOperador>();
    public DbSet<VersionReglasOperativas> VersionesReglasOperativas => Set<VersionReglasOperativas>();
    public DbSet<RequisitoPruebaEntrega> RequisitosPruebaEntrega => Set<RequisitoPruebaEntrega>();
    public DbSet<MotivoNoEntregaVersion> MotivosNoEntregaVersion => Set<MotivoNoEntregaVersion>();

    public DbSet<Rol> Roles => Set<Rol>();
    public DbSet<Permiso> Permisos => Set<Permiso>();
    public DbSet<RolPermiso> RolesPermisos => Set<RolPermiso>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfiguration(new CodigoCancelacionPublicaConfiguration());
        modelBuilder.ApplyConfiguration(new NexoRuta.Infrastructure.Envios.CancelacionEnvioConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.RepartidorConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.ConfiguracionRepartoConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.VehiculoConfiguration());
        modelBuilder.ApplyConfiguration(new Notificaciones.AvisoRepartoConfiguration());
        modelBuilder.ApplyConfiguration(new Notificaciones.InstalacionMovilConfiguration());
        modelBuilder.ApplyConfiguration(new Notificaciones.EntregaPushConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.HojaRutaConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.OperacionJornadaConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.EvidenciaRepartoConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.PruebaEntregaConfiguration());
        modelBuilder.ApplyConfiguration(new Reparto.ParadaRutaConfiguration());
        modelBuilder.ApplyConfiguration(new SesionMovilConfiguration());
        modelBuilder.ApplyConfiguration(new TokenRenovacionMovilConfiguration());
        modelBuilder.HasAnnotation("NexoRuta:RlsVersion", 1);
        modelBuilder.HasPostgresExtension("postgis");
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
            entity.Property(x => x.VersionCredencial).HasDefaultValue(0L).ValueGeneratedOnAdd().IsConcurrencyToken();
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
            entity.Property(x => x.Email).HasMaxLength(254).IsRequired();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Direccion>(entity =>
        {
            entity.ToTable("Direcciones");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Descripcion).HasMaxLength(240).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Envio>(entity =>
        {
            entity.ToTable("Envios", t =>
            {
                t.HasCheckConstraint("CK_Envios_Version", "\"Version\" >= 0");
                t.HasCheckConstraint("CK_Envios_EntregaFisica", "\"Estado\" NOT IN ('Entregado','EntregaPendienteValidacion','EntregaSinValidacion') OR \"EntregaFisicaEnUtc\" IS NOT NULL");
                t.HasCheckConstraint("CK_Envios_Cancelacion", "\"Estado\" <> 'Cancelado' OR \"EntregaFisicaEnUtc\" IS NULL");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<VersionReglasOperativas>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.VersionReglasId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
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
            entity.Property(x => x.Custodia).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<NexoRuta.Domain.Reparto.HojaRuta>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.RutaCustodiaId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<NexoRuta.Domain.Reparto.Repartidor>().WithMany()
                .HasForeignKey(x => new { x.AccesoCustodioId, x.OperadorId })
                .HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
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
            entity.ToTable("OutboxMessages", table =>
            {
                table.HasCheckConstraint("CK_OutboxMessages_Scope",
                    "(\"Ambito\" = 'Envio' AND \"OperadorId\" IS NOT NULL AND \"EnvioId\" IS NOT NULL AND \"ClaveAmbito\" = \"EnvioId\") OR " +
                    "(\"Ambito\" = 'Identidad' AND \"OperadorId\" IS NULL AND \"EnvioId\" IS NULL)");
                table.HasCheckConstraint("CK_OutboxMessages_Contract",
                    "(\"Tipo\" = 'GenerarSeguimiento' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR " +
                    "(\"Tipo\" = 'EnvioCreado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR " +
                    "(\"Tipo\" = 'RepartoModificado' AND \"VersionContrato\" = 1 AND \"Destino\" = 'PushFcm' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR " +
                    "(\"Tipo\" = 'CodigoCancelacionEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Envio' AND \"EstadoEntrega\" IS NULL) OR " +
                    "(\"Tipo\" = 'PasswordRecoveryEmail' AND \"VersionContrato\" = 1 AND \"Destino\" = 'RabbitMq' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IN ('Pending', 'Delivered', 'Expired', 'Failed')) OR " +
                    "(\"Tipo\" = 'PasswordRecoveryRequested' AND \"VersionContrato\" = 1 AND \"Destino\" = 'Kafka' AND \"Ambito\" = 'Identidad' AND \"EstadoEntrega\" IS NULL)");
                table.HasCheckConstraint("CK_OutboxMessages_Priority", "\"Prioridad\" IN ('Baja', 'Normal', 'Alta')");
                table.HasCheckConstraint("CK_OutboxMessages_State", "\"Estado\" IN ('Pendiente', 'Procesando', 'Publicado', 'Error')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Tipo).HasMaxLength(100).IsRequired();
            entity.Property(x => x.VersionContrato).IsRequired();
            entity.Property(x => x.Ambito).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ClaveAmbito).IsRequired();
            entity.Property(x => x.Destino).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Prioridad).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.EstadoEntrega).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.UltimoError).HasMaxLength(2000);
            entity.HasIndex(x => new { x.Destino, x.Estado, x.ProximoIntentoEnUtc, x.Prioridad, x.CreadoEnUtc });
            entity.HasIndex(x => new { x.Ambito, x.ClaveAmbito, x.Destino, x.Estado, x.CreadoEnUtc });
            entity.HasIndex(x => new { x.OperadorId, x.EnvioId });
            entity.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<ZonaCobertura>(entity =>
        {
            entity.ToTable("ZonasCobertura", table =>
            {
                table.HasCheckConstraint("CK_ZonasCobertura_State", "\"Estado\" IN ('Inactiva', 'Activa')");
                table.HasCheckConstraint("CK_ZonasCobertura_NameTimeZone",
                    "length(btrim(\"Nombre\")) > 0 AND \"ZonaHoraria\" = 'America/Montevideo'");
                table.HasCheckConstraint("CK_ZonasCobertura_Origin",
                    "(\"OrigenGeometria\" = 'Dibujada' AND \"CodigoDepartamento\" IS NULL AND \"VersionFuente\" IS NULL) OR " +
                    "(\"OrigenGeometria\" = 'Departamento' AND \"CodigoDepartamento\" IS NOT NULL AND \"VersionFuente\" IS NOT NULL)");
                table.HasCheckConstraint("CK_ZonasCobertura_Version", "\"Version\" >= 0 AND \"RevisionActual\" > 0");
                table.HasCheckConstraint("CK_ZonasCobertura_Geometria",
                    "NOT ST_IsEmpty(\"Geometria\") AND ST_NDims(\"Geometria\") = 2 AND ST_IsValid(\"Geometria\") AND " +
                    "ST_XMin(Box3D(\"Geometria\")) >= -180 AND ST_XMax(Box3D(\"Geometria\")) <= 180 AND " +
                    "ST_YMin(Box3D(\"Geometria\")) >= -90 AND ST_YMax(Box3D(\"Geometria\")) <= 90");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Nombre).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Geometria).HasColumnType("geometry(MultiPolygon,4326)").IsRequired();
            entity.Property(x => x.ZonaHoraria).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
            entity.Property(x => x.RevisionActual).IsRequired();
            entity.Property(x => x.OrigenGeometria).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(x => x.CodigoDepartamento).HasMaxLength(16);
            entity.Property(x => x.VersionFuente).HasMaxLength(80);
            entity.Property(x => x.CreadaUtc).IsRequired();
            entity.Property(x => x.ActualizadaUtc).IsRequired();
            entity.HasIndex(x => new { x.OperadorId, x.Estado });
            entity.HasIndex(x => x.Geometria).HasMethod("gist").HasDatabaseName("IX_ZonasCobertura_Geometria_Gist");
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<DepartamentoReferencia>().WithMany()
                .HasForeignKey(x => new { x.CodigoDepartamento, x.VersionFuente })
                .HasPrincipalKey(x => new { x.Codigo, x.VersionFuente })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Franjas).WithOne()
                .HasForeignKey(x => new { x.OperadorId, x.ZonaId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.Navigation(x => x.Franjas).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<FranjaEntrega>(entity =>
        {
            entity.ToTable("FranjasEntrega", table =>
            {
                table.HasCheckConstraint("CK_FranjasEntrega_Day", "\"DiaSemanaIso\" BETWEEN 1 AND 7");
                table.HasCheckConstraint("CK_FranjasEntrega_Time", "\"HoraInicio\" <> \"HoraFin\"");
                table.HasCheckConstraint("CK_FranjasEntrega_State", "\"Estado\" IN ('Inactiva', 'Activa')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.DiaSemanaIso).HasConversion<short>().HasColumnType("smallint").IsRequired();
            entity.Property(x => x.HoraInicio).HasColumnType("time(6) without time zone").IsRequired();
            entity.Property(x => x.HoraFin).HasColumnType("time(6) without time zone").IsRequired();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.HasIndex(x => new { x.OperadorId, x.ZonaId, x.DiaSemanaIso, x.HoraInicio });
        });

        modelBuilder.Entity<ZonaCoberturaRevision>(entity =>
        {
            entity.ToTable("ZonasCoberturaRevisiones", table =>
            {
                table.HasCheckConstraint("CK_ZonasCoberturaRevisiones_Revision", "\"NumeroRevision\" > 0 AND \"Version\" >= 0");
                table.HasCheckConstraint("CK_ZonasCoberturaRevisiones_Snapshot", "jsonb_typeof(\"SnapshotJson\") = 'object'");
            });
            entity.HasKey(x => new { x.OperadorId, x.ZonaId, x.NumeroRevision });
            entity.Property(x => x.RegistradaUtc).IsRequired();
            entity.Property(x => x.SnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.HasOne<ZonaCobertura>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.ZonaId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DepartamentoReferencia>(entity =>
        {
            entity.ToTable("DepartamentosReferencia", table =>
                table.HasCheckConstraint("CK_DepartamentosReferencia_Geometria",
                    "NOT ST_IsEmpty(\"Geometria\") AND ST_NDims(\"Geometria\") = 2 AND ST_IsValid(\"Geometria\") AND " +
                    "ST_XMin(Box3D(\"Geometria\")) >= -180 AND ST_XMax(Box3D(\"Geometria\")) <= 180 AND " +
                    "ST_YMin(Box3D(\"Geometria\")) >= -90 AND ST_YMax(Box3D(\"Geometria\")) <= 90"));
            entity.HasKey(x => new { x.Codigo, x.VersionFuente });
            entity.Property(x => x.Codigo).HasMaxLength(16).IsRequired();
            entity.Property(x => x.VersionFuente).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Nombre).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Geometria).HasColumnType("geometry(MultiPolygon,4326)").IsRequired();
            entity.Property(x => x.UrlFuente).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Atribucion).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Licencia).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ImportadaUtc).IsRequired();
            entity.HasIndex(x => x.Geometria).HasMethod("gist").HasDatabaseName("IX_DepartamentosReferencia_Geometria_Gist");
        });

        modelBuilder.Entity<TarifarioOperador>(entity =>
        {
            entity.ToTable("TarifariosOperador", table =>
                table.HasCheckConstraint("CK_TarifariosOperador_PublicationCounter", "\"UltimoNumeroPublicacion\" >= 0"));
            entity.HasKey(x => x.OperadorId);
            entity.Property(x => x.UltimoNumeroPublicacion).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.ActualizadoUtc).IsRequired();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<VersionTarifario>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.VersionVigenteId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VersionTarifario>(entity =>
        {
            entity.ToTable("VersionesTarifario", table =>
            {
                table.HasCheckConstraint("CK_VersionesTarifario_State",
                    "\"Estado\" IN ('Borrador', 'Publicada')");
                table.HasCheckConstraint("CK_VersionesTarifario_Publication",
                    "(\"Estado\" = 'Borrador' AND \"NumeroVersionPublicada\" IS NULL AND \"PublicadaUtc\" IS NULL AND \"PublicadaPorUsuarioId\" IS NULL) OR " +
                    "(\"Estado\" = 'Publicada' AND \"NumeroVersionPublicada\" > 0 AND \"PublicadaUtc\" IS NOT NULL AND \"PublicadaPorUsuarioId\" IS NOT NULL)");
                table.HasCheckConstraint("CK_VersionesTarifario_Version", "\"VersionConcurrencia\" >= 0");
                table.HasCheckConstraint("CK_VersionesTarifario_Currency", "\"Moneda\" = 'UYU'");
            });
            entity.HasKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.NumeroVersionPublicada);
            entity.Property(x => x.VersionConcurrencia).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
            entity.Property(x => x.Moneda).HasConversion<string>().HasMaxLength(3).IsRequired();
            entity.Property(x => x.CreadaUtc).IsRequired();
            entity.Property(x => x.PublicadaUtc);
            entity.HasIndex(x => x.OperadorId).IsUnique()
                .HasFilter("\"Estado\" = 'Borrador'").HasDatabaseName("UX_VersionesTarifario_OneDraftPerOperator");
            entity.HasIndex(x => new { x.OperadorId, x.NumeroVersionPublicada }).IsUnique()
                .HasFilter("\"NumeroVersionPublicada\" IS NOT NULL").HasDatabaseName("UX_VersionesTarifario_PublicationNumber");
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.CreadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.PublicadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Tarifas).WithOne()
                .HasForeignKey(x => new { x.OperadorId, x.VersionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(x => x.Tarifas).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<TarifaZonaModalidad>(entity =>
        {
            entity.ToTable("TarifasZonaModalidad", table =>
            {
                table.HasCheckConstraint("CK_TarifasZonaModalidad_Modality", "\"Modalidad\" IN ('Estandar', 'Urgente')");
                table.HasCheckConstraint("CK_TarifasZonaModalidad_Amounts",
                    "\"ImporteBaseUyu\" >= 0 AND \"PrecioPorKilogramoUyu\" >= 0 AND \"PrecioPorMetroCubicoUyu\" >= 0");
            });
            entity.HasKey(x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad });
            entity.Property(x => x.Modalidad).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.ImporteBaseUyu).HasPrecision(ReglasTarifario.MoneyPrecision, ReglasTarifario.MoneyScale);
            entity.Property(x => x.PrecioPorKilogramoUyu).HasPrecision(ReglasTarifario.RatePrecision, ReglasTarifario.RateScale);
            entity.Property(x => x.PrecioPorMetroCubicoUyu).HasPrecision(ReglasTarifario.RatePrecision, ReglasTarifario.RateScale);
            entity.HasOne<VersionTarifario>().WithMany(x => x.Tarifas)
                .HasForeignKey(x => new { x.OperadorId, x.VersionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ZonaCobertura>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.ZonaId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Ajustes).WithOne()
                .HasForeignKey(x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad })
                .HasPrincipalKey(x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad })
                .OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(x => x.Ajustes).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<AjusteTarifario>(entity =>
        {
            entity.ToTable("AjustesTarifario", table =>
            {
                table.HasCheckConstraint("CK_AjustesTarifario_Type", "\"Tipo\" IN ('RecargoFijo', 'RecargoPorcentual', 'BonificacionFija', 'BonificacionPorcentual')");
                table.HasCheckConstraint("CK_AjustesTarifario_Value",
                    "((\"Tipo\" IN ('RecargoFijo', 'BonificacionFija') AND \"ImporteFijoUyu\" IS NOT NULL AND \"Porcentaje\" IS NULL AND \"ImporteFijoUyu\" >= 0) OR " +
                    "(\"Tipo\" IN ('RecargoPorcentual', 'BonificacionPorcentual') AND \"ImporteFijoUyu\" IS NULL AND \"Porcentaje\" BETWEEN 0 AND 100))");
                table.HasCheckConstraint("CK_AjustesTarifario_Name", "length(btrim(\"Nombre\")) > 0");
            });
            entity.HasKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Modalidad).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Nombre).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.ImporteFijoUyu).HasPrecision(ReglasTarifario.MoneyPrecision, ReglasTarifario.MoneyScale);
            entity.Property(x => x.Porcentaje).HasPrecision(ReglasTarifario.PercentagePrecision, ReglasTarifario.PercentageScale);
            entity.HasOne<TarifaZonaModalidad>().WithMany(x => x.Ajustes)
                .HasForeignKey(x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad })
                .HasPrincipalKey(x => new { x.OperadorId, x.VersionId, x.ZonaId, x.Modalidad })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReglasOperativasOperador>(entity =>
        {
            entity.ToTable("ReglasOperativasOperador", table => table.HasCheckConstraint(
                "CK_ReglasOperativasOperador_PublicationCounter", "\"UltimoNumeroPublicacion\" >= 0"));
            entity.HasKey(x => x.OperadorId);
            entity.Property(x => x.UltimoNumeroPublicacion).HasDefaultValue(0).IsRequired();
            entity.Property(x => x.ActualizadoUtc).IsRequired();
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<VersionReglasOperativas>().WithMany()
                .HasForeignKey(x => new { x.OperadorId, x.VersionVigenteId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VersionReglasOperativas>(entity =>
        {
            entity.ToTable("VersionesReglasOperativas", table =>
            {
                table.HasCheckConstraint("CK_VersionesReglasOperativas_State",
                    "\"Estado\" IN ('Borrador', 'Publicada')");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_Publication",
                    "(\"Estado\" = 'Borrador' AND \"NumeroVersionPublicada\" IS NULL AND \"PublicadaUtc\" IS NULL AND \"PublicadaPorUsuarioId\" IS NULL) OR " +
                    "(\"Estado\" = 'Publicada' AND \"NumeroVersionPublicada\" > 0 AND \"PublicadaUtc\" IS NOT NULL AND \"PublicadaPorUsuarioId\" IS NOT NULL)");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_Concurrency", "\"VersionConcurrencia\" >= 0");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_MaxAttempts",
                    "\"MaxIntentosEntrega\" IS NULL OR \"MaxIntentosEntrega\" BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_Interval",
                    "\"IntervaloEntreIntentosMinutos\" IS NULL OR \"IntervaloEntreIntentosMinutos\" BETWEEN 1 AND 525600");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_StandardDeadline",
                    "\"PlazoEstandarMinutos\" IS NULL OR \"PlazoEstandarMinutos\" BETWEEN 1 AND 525600");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_UrgentDeadline",
                    "\"PlazoUrgenteMinutos\" IS NULL OR \"PlazoUrgenteMinutos\" BETWEEN 1 AND 525600");
                table.HasCheckConstraint("CK_VersionesReglasOperativas_ReturnPolicy",
                    "\"PoliticaDevolucionAlAgotarIntentos\" IS NULL OR \"PoliticaDevolucionAlAgotarIntentos\" IN ('DecisionOperativa', 'IniciarAutomaticamente')");
            });
            entity.HasKey(x => new { x.OperadorId, x.Id });
            entity.Property(x => x.Id).ValueGeneratedNever();
            entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.VersionConcurrencia).HasDefaultValue(0L).IsConcurrencyToken().ValueGeneratedNever();
            entity.Property(x => x.PoliticaDevolucionAlAgotarIntentos).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreadaUtc).IsRequired();
            entity.HasIndex(x => x.OperadorId).IsUnique()
                .HasFilter("\"Estado\" = 'Borrador'")
                .HasDatabaseName("UX_VersionesReglasOperativas_OneDraftPerOperator");
            entity.HasIndex(x => new { x.OperadorId, x.NumeroVersionPublicada }).IsUnique()
                .HasFilter("\"NumeroVersionPublicada\" IS NOT NULL")
                .HasDatabaseName("UX_VersionesReglasOperativas_PublicationNumber");
            entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.CreadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.PublicadaPorUsuarioId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.RequisitosPrueba).WithOne()
                .HasForeignKey(x => new { x.OperadorId, x.VersionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Motivos).WithOne()
                .HasForeignKey(x => new { x.OperadorId, x.VersionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.Navigation(x => x.RequisitosPrueba).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(x => x.Motivos).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<RequisitoPruebaEntrega>(entity =>
        {
            entity.ToTable("RequisitosPruebaEntrega", table => table.HasCheckConstraint(
                "CK_RequisitosPruebaEntrega_Type",
                "\"Tipo\" IN ('FirmaEnPantalla', 'Fotografia', 'NombreReceptor', 'DocumentoReceptor')"));
            entity.HasKey(x => new { x.OperadorId, x.VersionId, x.Tipo });
            entity.Property(x => x.Tipo).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.HasOne<VersionReglasOperativas>().WithMany(x => x.RequisitosPrueba)
                .HasForeignKey(x => new { x.OperadorId, x.VersionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MotivoNoEntregaVersion>(entity =>
        {
            entity.ToTable("MotivosNoEntregaVersion", table =>
            {
                table.HasCheckConstraint("CK_MotivosNoEntregaVersion_Code",
                    "\"Codigo\" ~ '^[A-Z][A-Z0-9_]{0,39}$'");
                table.HasCheckConstraint("CK_MotivosNoEntregaVersion_Description",
                    "length(btrim(\"Descripcion\")) BETWEEN 1 AND 120 AND \"Descripcion\" !~ '[[:cntrl:]]'");
            });
            entity.HasKey(x => new { x.OperadorId, x.VersionId, x.Codigo });
            entity.Property(x => x.Codigo).HasMaxLength(NexoRuta.Domain.ReglasOperativas.ReglasOperativas.MotiveCodeMaxLength).IsRequired();
            entity.Property(x => x.Descripcion).HasMaxLength(NexoRuta.Domain.ReglasOperativas.ReglasOperativas.MotiveDescriptionMaxLength).IsRequired();
            entity.Property(x => x.Disponible).IsRequired();
            entity.HasOne<VersionReglasOperativas>().WithMany(x => x.Motivos)
                .HasForeignKey(x => new { x.OperadorId, x.VersionId })
                .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
