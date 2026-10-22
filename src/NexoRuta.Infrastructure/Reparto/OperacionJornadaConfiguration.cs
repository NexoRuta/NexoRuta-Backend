using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.Envios;
namespace NexoRuta.Infrastructure.Reparto;
public sealed class OperacionJornadaConfiguration : IEntityTypeConfiguration<OperacionJornada>
{
    public void Configure(EntityTypeBuilder<OperacionJornada> e)
    {
        e.ToTable("OperacionesJornada"); e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasIndex(x => new { x.OperadorId, x.RutaId, x.Secuencia }).IsUnique();
        e.Property(x => x.Hash).HasMaxLength(64); e.Property(x => x.Tipo).HasMaxLength(40);
        e.Property(x => x.Resultado).HasMaxLength(30); e.Property(x => x.Payload).HasColumnType("jsonb");
        e.Property(x => x.Respuesta).HasColumnType("jsonb");
        e.Property(x => x.IncidenciasCierre).HasColumnType("jsonb");
        e.HasOne<HojaRuta>().WithMany().HasForeignKey(x => new { x.OperadorId, x.RutaId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Repartidor>().WithMany().HasForeignKey(x => new { x.AccesoId, x.OperadorId }).HasPrincipalKey(x => new { x.AccesoId, x.OperadorId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
