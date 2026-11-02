using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class OcupacionRutaConfiguration : IEntityTypeConfiguration<OcupacionRuta>
{
    public void Configure(EntityTypeBuilder<OcupacionRuta> e)
    {
        e.ToTable("OcupacionesRuta", t => t.HasCheckConstraint("CK_OcupacionesRuta_Hechos",
            "\"Version\" >= 0 AND (\"LiberadaEnUtc\" IS NULL OR (\"RetornoEnUtc\" IS NOT NULL AND \"AnuladaEnUtc\" IS NULL))"));
        e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasAlternateKey(x => new { x.OperadorId, x.Id });
        e.HasIndex(x => new { x.OperadorId, x.RutaId }).IsUnique();
        e.HasIndex(x => new { x.OperadorId, x.VehiculoId }).IsUnique()
            .HasFilter("\"LiberadaEnUtc\" IS NULL AND \"AnuladaEnUtc\" IS NULL");
        e.HasIndex(x => new { x.OperadorId, x.RepartidorAccesoId }).IsUnique()
            .HasFilter("\"LiberadaEnUtc\" IS NULL AND \"AnuladaEnUtc\" IS NULL");
        e.Property(x => x.Version).IsConcurrencyToken(); e.Ignore(x => x.Activa);
        e.HasOne<HojaRuta>().WithMany().HasForeignKey(x => new { x.OperadorId, x.RutaId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Vehiculo>().WithMany().HasForeignKey(x => new { x.OperadorId, x.VehiculoId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Repartidor>().WithMany().HasForeignKey(x => new { x.RepartidorAccesoId, x.OperadorId })
            .HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class HechoFlotaConfiguration : IEntityTypeConfiguration<HechoFlota>
{
    public void Configure(EntityTypeBuilder<HechoFlota> e)
    {
        e.ToTable("HechosFlota"); e.HasKey(x => new { x.OperadorId, x.ComandoId });
        e.Property(x => x.Accion).HasMaxLength(40); e.Property(x => x.Hash).HasMaxLength(64);
        e.Property(x => x.Motivo).HasMaxLength(500);
        e.Property(x => x.Respuesta).HasColumnType("jsonb");
        e.HasOne<Vehiculo>().WithMany().HasForeignKey(x => new { x.OperadorId, x.VehiculoId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OcupacionRuta>().WithMany().HasForeignKey(x => new { x.OperadorId, x.OcupacionId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
