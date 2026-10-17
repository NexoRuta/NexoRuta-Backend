using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Reparto;
public sealed class EvidenciaRepartoConfiguration : IEntityTypeConfiguration<EvidenciaReparto>
{
    public void Configure(EntityTypeBuilder<EvidenciaReparto> e)
    {
        e.ToTable("EvidenciasReparto"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasAlternateKey(x => new { x.OperadorId, x.EnvioId, x.Id });
        e.Property(x => x.Tipo).HasMaxLength(10); e.Property(x => x.Hash).HasMaxLength(64);
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<HojaRuta>().WithMany().HasForeignKey(x => new { x.OperadorId, x.RutaId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Repartidor>().WithMany().HasForeignKey(x => new { x.AccesoId, x.OperadorId }).HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
    }
}
