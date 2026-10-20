using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Notificaciones;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class AvisoRepartoConfiguration : IEntityTypeConfiguration<AvisoReparto>
{
    public void Configure(EntityTypeBuilder<AvisoReparto> e)
    {
        e.ToTable("AvisosReparto"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasAlternateKey(x => new { x.OperadorId, x.Id });
        e.Property(x => x.Tipo).HasMaxLength(20); e.Property(x => x.Motivo).HasMaxLength(500);
        e.HasIndex(x => new { x.OperadorId, x.AccesoId, x.CreadoEnUtc });
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<HojaRuta>().WithMany().HasForeignKey(x => new { x.OperadorId, x.RutaId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Repartidor>().WithMany().HasForeignKey(x => new { x.AccesoId, x.OperadorId }).HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
    }
}
