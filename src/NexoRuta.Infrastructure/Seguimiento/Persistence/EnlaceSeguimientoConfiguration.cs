using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Seguimiento;

namespace NexoRuta.Infrastructure.Seguimiento.Persistence;

public sealed class EnlaceSeguimientoConfiguration
    : IEntityTypeConfiguration<EnlaceSeguimiento>
{
    public void Configure(
        EntityTypeBuilder<EnlaceSeguimiento> entity)
    {
        entity.ToTable("EnlacesSeguimiento");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.TokenHash)
            .HasMaxLength(64)
            .IsRequired();

        entity.HasIndex(x => x.TokenHash)
            .IsUnique();

        entity.HasOne<Envio>()
            .WithMany()
            .HasForeignKey(x => new
            {
                x.OperadorId,
                x.EnvioId
            })
            .HasPrincipalKey(x => new
            {
                x.OperadorId,
                x.Id
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}