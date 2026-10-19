using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class VehiculoConfiguration : IEntityTypeConfiguration<Vehiculo>
{
    public void Configure(EntityTypeBuilder<Vehiculo> entity)
    {
        entity.ToTable("Vehiculos", t => t.HasCheckConstraint("CK_Vehiculos_Capacidad", "\"PesoMaximoGramos\" > 0 AND \"VolumenMaximoCm3\" > 0"));
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.HasAlternateKey(x => new { x.OperadorId, x.Id });
        entity.HasIndex(x => new { x.OperadorId, x.Matricula }).IsUnique();
        entity.Property(x => x.Matricula).HasMaxLength(20);
        entity.Property(x => x.PesoMaximoGramos).HasPrecision(18, 2);
        entity.Property(x => x.VolumenMaximoCm3).HasPrecision(24, 2);
        entity.Property(x => x.Version).IsConcurrencyToken();
        entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
    }
}
