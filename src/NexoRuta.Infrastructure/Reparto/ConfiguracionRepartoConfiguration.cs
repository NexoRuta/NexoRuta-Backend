using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class ConfiguracionRepartoConfiguration : IEntityTypeConfiguration<ConfiguracionReparto>
{
    public void Configure(EntityTypeBuilder<ConfiguracionReparto> entity)
    {
        entity.ToTable("ConfiguracionesReparto", t =>
        {
            t.HasCheckConstraint("CK_ConfiguracionesReparto_Version", "\"Version\" > 0");
            t.HasCheckConstraint("CK_ConfiguracionesReparto_Limites", "\"MaximoParadas\" BETWEEN 1 AND 500");
        });
        entity.HasKey(x => new { x.OperadorId, x.Version });
        entity.Property(x => x.ZonaHoraria).HasMaxLength(100);
        entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
    }
}
