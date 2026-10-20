using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class ParadaRutaConfiguration : IEntityTypeConfiguration<ParadaRuta>
{
    public void Configure(EntityTypeBuilder<ParadaRuta> entity)
    {
        entity.ToTable("ParadasRuta", t => t.HasCheckConstraint("CK_ParadasRuta_Orden", "\"Orden\" > 0"));
        entity.HasKey(x => new { x.RutaId, x.EnvioId });
        entity.Property(x => x.MotivoSuspension).HasMaxLength(500);
        entity.HasIndex(x => new { x.OperadorId, x.EnvioId }).IsUnique().HasFilter("\"Activa\" = TRUE");
        entity.HasIndex(x => new { x.RutaId, x.Orden }).IsUnique();
        entity.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
