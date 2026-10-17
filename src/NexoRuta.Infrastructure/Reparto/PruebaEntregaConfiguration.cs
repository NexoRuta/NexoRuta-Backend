using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Reparto;
public sealed class PruebaEntregaConfiguration : IEntityTypeConfiguration<PruebaEntrega>
{
    public void Configure(EntityTypeBuilder<PruebaEntrega> e)
    {
        e.ToTable("PruebasEntrega"); e.HasKey(x => new { x.OperadorId, x.EnvioId });
        e.Property(x => x.Nombre).HasMaxLength(160); e.Property(x => x.Documento).HasMaxLength(40);
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<HojaRuta>().WithMany().HasForeignKey(x => new { x.OperadorId, x.RutaId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<EvidenciaReparto>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId, x.FotoId }).HasPrincipalKey(x => new { x.OperadorId, x.EnvioId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<EvidenciaReparto>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId, x.FirmaId }).HasPrincipalKey(x => new { x.OperadorId, x.EnvioId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
