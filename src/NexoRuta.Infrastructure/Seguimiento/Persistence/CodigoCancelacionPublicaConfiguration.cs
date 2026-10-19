using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.Infrastructure.Seguimiento.Persistence;

public sealed class CodigoCancelacionPublicaConfiguration : IEntityTypeConfiguration<CodigoCancelacionPublica>
{
    public void Configure(EntityTypeBuilder<CodigoCancelacionPublica> e)
    {
        e.ToTable("CodigosCancelacionPublica", t => t.HasCheckConstraint("CK_CodigosCancelacionPublica_Intentos", "\"Intentos\" BETWEEN 0 AND 5"));
        e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasIndex(x => x.MensajeId).IsUnique();
        e.Property(x => x.Hash).HasMaxLength(100).IsRequired();
        e.HasAlternateKey(x => new { x.OperadorId, x.EnvioId, x.Id });
        e.HasIndex(x => new { x.OperadorId, x.EnvioId }).IsUnique().HasFilter("\"Activo\" = TRUE");
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId }).HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<OutboxMessage>().WithMany().HasForeignKey(x => x.MensajeId).OnDelete(DeleteBehavior.Restrict);
    }
}
