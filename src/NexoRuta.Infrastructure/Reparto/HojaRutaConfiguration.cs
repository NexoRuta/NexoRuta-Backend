using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class HojaRutaConfiguration : IEntityTypeConfiguration<HojaRuta>
{
    public void Configure(EntityTypeBuilder<HojaRuta> entity)
    {
        entity.ToTable("HojasRuta", t => t.HasCheckConstraint("CK_HojasRuta_Secuencia", "\"UltimaSecuencia\" >= 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
        entity.HasIndex(x => x.JornadaId).IsUnique();
        entity.Property(x => x.Estado).HasConversion<string>().HasMaxLength(30);
        entity.Property(x => x.Version).IsConcurrencyToken();
        entity.Property(x => x.ReservaRecursosActiva).HasDefaultValue(true);
        entity.Property(x => x.VehiculoMatricula).HasMaxLength(20);
        entity.Property(x => x.VehiculoPesoMaximoGramos).HasPrecision(18, 2);
        entity.Property(x => x.VehiculoVolumenMaximoCm3).HasPrecision(24, 2);
        entity.HasIndex(x => new { x.OperadorId, x.RepartidorAccesoId, x.Fecha }).IsUnique()
            .HasFilter("\"Estado\" NOT IN ('Finalizada', 'Cancelada') AND \"ReservaRecursosActiva\"");
        entity.HasIndex(x => new { x.OperadorId, x.VehiculoId, x.Fecha }).IsUnique()
            .HasFilter("\"Estado\" NOT IN ('Finalizada', 'Cancelada') AND \"ReservaRecursosActiva\"");
        entity.HasOne<Repartidor>().WithMany().HasForeignKey(x => new { x.RepartidorAccesoId, x.OperadorId })
            .HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Vehiculo>().WithMany().HasForeignKey(x => new { x.OperadorId, x.VehiculoId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ConfiguracionReparto>().WithMany().HasForeignKey(x => new { x.OperadorId, x.ConfiguracionVersion })
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasMany(x => x.Paradas).WithOne().HasForeignKey(x => new { x.OperadorId, x.RutaId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.Navigation(x => x.Paradas).HasField("paradas").UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
