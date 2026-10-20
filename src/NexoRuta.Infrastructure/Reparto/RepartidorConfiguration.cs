using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class RepartidorConfiguration : IEntityTypeConfiguration<Repartidor>
{
    public void Configure(EntityTypeBuilder<Repartidor> entity)
    {
        entity.ToTable("Repartidores", table =>
        {
            table.HasCheckConstraint("CK_Repartidores_Operador", "\"TipoOrganizacion\" = 'Operador'");
            table.HasCheckConstraint("CK_Repartidores_Version", "\"Version\" >= 0");
            table.HasCheckConstraint("CK_Repartidores_Nombre", "length(btrim(\"Nombre\")) > 0");
        });
        entity.HasKey(x => x.AccesoId);
        entity.Property(x => x.AccesoId).ValueGeneratedNever();
        entity.Property(x => x.Nombre).HasMaxLength(120).IsRequired();
        entity.Property(x => x.Version).IsConcurrencyToken();
        entity.Property(x => x.TipoOrganizacion).HasConversion<string>().HasMaxLength(20);
        entity.HasIndex(x => new { x.OperadorId, x.Activo });
        entity.HasOne<Operador>().WithMany().HasForeignKey(x => x.OperadorId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<AccesoUsuario>().WithMany()
            .HasForeignKey(x => new { x.AccesoId, x.TipoOrganizacion, x.OperadorId })
            .HasPrincipalKey(x => new { x.Id, x.TipoOrganizacion, x.OrganizacionId }).OnDelete(DeleteBehavior.Restrict);
    }
}
