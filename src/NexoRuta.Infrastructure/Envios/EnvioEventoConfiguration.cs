using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Infrastructure.Envios;

public sealed class EnvioEventoConfiguration : IEntityTypeConfiguration<EnvioEvento>
{
    public void Configure(EntityTypeBuilder<EnvioEvento> e)
    {
        e.ToTable("EnvioEventos", t =>
        {
            t.HasCheckConstraint("CK_EnvioEventos_Tipo",
                "\"Tipo\" IN ('Creacion','TransicionEstado','EstadoObservadoInicio','Hecho')");
            t.HasCheckConstraint("CK_EnvioEventos_Procedencia", "\"Procedencia\" IN ('EnLinea','Corte','Backfill')");
            t.HasCheckConstraint("CK_EnvioEventos_Forma",
                "(\"Tipo\" = 'Creacion' AND \"EstadoAnterior\" IS NULL AND \"EstadoNuevo\" = 'Admitido' AND \"EstadoObservado\" IS NULL AND \"VersionAnterior\" IS NULL AND \"VersionNueva\" = 0 AND \"CodigoHecho\" IS NULL) OR " +
                "(\"Tipo\" = 'TransicionEstado' AND \"EstadoAnterior\" IS NOT NULL AND \"EstadoNuevo\" IS NOT NULL AND \"EstadoAnterior\" <> \"EstadoNuevo\" AND \"EstadoObservado\" IS NULL AND \"VersionAnterior\" >= 0 AND \"VersionNueva\" > \"VersionAnterior\" AND \"CodigoHecho\" IS NULL) OR " +
                "(\"Tipo\" = 'EstadoObservadoInicio' AND \"EstadoAnterior\" IS NULL AND \"EstadoNuevo\" IS NULL AND \"EstadoObservado\" IS NOT NULL AND \"VersionAnterior\" IS NULL AND \"CodigoHecho\" IS NULL) OR " +
                "(\"Tipo\" = 'Hecho' AND \"EstadoAnterior\" IS NULL AND \"EstadoNuevo\" IS NULL AND \"EstadoObservado\" IS NOT NULL AND \"VersionAnterior\" >= 0 AND \"VersionNueva\" > \"VersionAnterior\" AND \"CodigoHecho\" IS NOT NULL)");
            t.HasCheckConstraint("CK_EnvioEventos_Actor",
                "(\"ActorTipo\" = 'Usuario' AND \"ActorUsuarioId\" IS NOT NULL AND \"ActorProceso\" IS NULL AND \"AutorizacionPublicaId\" IS NULL) OR " +
                "(\"ActorTipo\" = 'Publico' AND \"ActorUsuarioId\" IS NULL AND \"ActorAccesoId\" IS NULL AND \"ActorProceso\" IS NULL AND \"AutorizacionPublicaId\" IS NOT NULL) OR " +
                "(\"ActorTipo\" IN ('Proceso','Migracion') AND \"ActorUsuarioId\" IS NULL AND \"ActorAccesoId\" IS NULL AND \"ActorProceso\" IS NOT NULL AND \"AutorizacionPublicaId\" IS NULL)");
            t.HasCheckConstraint("CK_EnvioEventos_OrigenCancelacion",
                "\"CancelacionEnvioId\" IS NULL OR (\"Tipo\" = 'TransicionEstado' AND \"EstadoNuevo\" = 'Cancelado' AND \"OrigenTipo\" = 'CancelacionEnvio' AND \"OrigenId\" = \"CancelacionEnvioId\")");
        });

        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedOnAdd().HasDefaultValueSql("gen_random_uuid()");
        e.Property(x => x.Tipo).HasMaxLength(32).IsRequired();
        e.Property(x => x.EstadoAnterior).HasMaxLength(40);
        e.Property(x => x.EstadoNuevo).HasMaxLength(40);
        e.Property(x => x.EstadoObservado).HasMaxLength(40);
        e.Property(x => x.ActorTipo).HasMaxLength(20).IsRequired();
        e.Property(x => x.ActorProceso).HasMaxLength(128);
        e.Property(x => x.OrigenTipo).HasMaxLength(48).IsRequired();
        e.Property(x => x.CodigoHecho).HasMaxLength(40);
        e.Property(x => x.Procedencia).HasMaxLength(16).IsRequired();
        e.Property(x => x.OcurridaEnUtc).HasColumnType("timestamp with time zone");
        e.Property(x => x.RegistradaEnUtc).HasColumnType("timestamp with time zone")
            .HasDefaultValueSql("clock_timestamp()");

        e.HasOne<Envio>().WithMany()
            .HasForeignKey(x => new { x.OperadorId, x.EnvioId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Envio>().WithMany()
            .HasForeignKey(x => new { x.ComercioId, x.EnvioId })
            .HasPrincipalKey(x => new { x.ComercioId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<VersionReglasOperativas>().WithMany()
            .HasForeignKey(x => new { x.OperadorId, x.VersionReglasId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<CancelacionEnvio>().WithMany()
            .HasForeignKey(x => x.CancelacionEnvioId).OnDelete(DeleteBehavior.Restrict);

        e.HasIndex(x => new { x.OperadorId, x.EnvioId, x.VersionAnterior, x.VersionNueva })
            .IsUnique().HasFilter("\"Tipo\" = 'TransicionEstado'");
        e.HasIndex(x => new { x.OperadorId, x.EnvioId })
            .IsUnique().HasFilter("\"Tipo\" = 'Creacion'");
        e.HasIndex(x => new { x.OperadorId, x.EnvioId })
            .IsUnique().HasFilter("\"Tipo\" = 'EstadoObservadoInicio'");
        e.HasIndex(x => new { x.OperadorId, x.EnvioId, x.OrigenTipo, x.OrigenId, x.CodigoHecho })
            .IsUnique().HasFilter("\"Tipo\" = 'Hecho' AND \"OrigenId\" IS NOT NULL");
        e.HasIndex(x => x.CancelacionEnvioId).IsUnique().HasFilter("\"CancelacionEnvioId\" IS NOT NULL");
        e.HasIndex(x => new { x.ComercioId, x.EnvioId, x.RegistradaEnUtc });
        e.HasIndex(x => new { x.OperadorId, x.EnvioId, x.RegistradaEnUtc });
    }
}
