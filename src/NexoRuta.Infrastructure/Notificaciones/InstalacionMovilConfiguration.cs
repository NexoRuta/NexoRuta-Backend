using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class InstalacionMovilConfiguration : IEntityTypeConfiguration<InstalacionMovil>
{
    public void Configure(EntityTypeBuilder<InstalacionMovil> e)
    {
        e.ToTable("InstalacionesMoviles"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasAlternateKey(x => new { x.OperadorId, x.Id });
        e.HasIndex(x => new { x.OperadorId, x.AccesoId, x.FidHash });
        e.Property(x => x.FidHash).HasMaxLength(64); e.Property(x => x.Plataforma).HasMaxLength(10);
        e.HasOne<SesionMovil>().WithMany().HasForeignKey(x => new { x.SesionId, x.AccesoId }).HasPrincipalKey(x => new { x.Id, x.AccesoId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Repartidor>().WithMany().HasForeignKey(x => new { x.AccesoId, x.OperadorId }).HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
    }
}
