using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.Administracion;
namespace NexoRuta.Infrastructure.Reparto;
public sealed class BaseOperativaConfiguration : IEntityTypeConfiguration<BaseOperativa>
{
    public void Configure(EntityTypeBuilder<BaseOperativa> e)
    {
        e.ToTable("BasesOperativas", t => { t.HasCheckConstraint("CK_Base_Tiempos", "(\"ServicioPredeterminadoMinutos\" IS NULL OR \"ServicioPredeterminadoMinutos\" >= 0) AND (\"MargenRetornoMinutos\" IS NULL OR \"MargenRetornoMinutos\" >= 0)");
            t.HasCheckConstraint("CK_Base_Punto", "(\"Latitud\" IS NULL AND \"Longitud\" IS NULL AND \"ConfirmadaEnUtc\" IS NULL AND \"ConfirmadaPorAccesoId\" IS NULL) OR (\"Latitud\" IS NOT NULL AND \"Longitud\" IS NOT NULL AND \"Latitud\" BETWEEN -90 AND 90 AND \"Longitud\" BETWEEN -180 AND 180 AND \"ConfirmadaEnUtc\" IS NOT NULL AND \"ConfirmadaPorAccesoId\" IS NOT NULL)"); });
        e.HasKey(b => b.OperadorId); e.Property(b => b.OperadorId).ValueGeneratedNever();
        e.Property(b => b.Direccion).HasMaxLength(500); e.Property(b => b.Version).IsConcurrencyToken();
        e.HasOne<Operador>().WithMany().HasForeignKey(b => b.OperadorId).OnDelete(DeleteBehavior.Restrict);
    }
}
