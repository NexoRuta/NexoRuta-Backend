using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Notificaciones;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class EntregaPushConfiguration : IEntityTypeConfiguration<EntregaPush>
{
    public void Configure(EntityTypeBuilder<EntregaPush> e)
    {
        e.ToTable("EntregasPush"); e.HasKey(x => new { x.AvisoId, x.InstalacionId, x.FidHash });
        e.Property(x => x.FidHash).HasMaxLength(64); e.Property(x => x.Resultado).HasMaxLength(30); e.Property(x => x.IdFcm).HasMaxLength(500);
        e.HasOne<AvisoReparto>().WithMany().HasForeignKey(x => new { x.OperadorId, x.AvisoId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<InstalacionMovil>().WithMany().HasForeignKey(x => new { x.OperadorId, x.InstalacionId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
