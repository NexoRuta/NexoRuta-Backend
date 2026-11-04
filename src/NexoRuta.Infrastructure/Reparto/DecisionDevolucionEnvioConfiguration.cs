using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class DecisionDevolucionEnvioConfiguration : IEntityTypeConfiguration<DecisionDevolucionEnvio>
{
    public void Configure(EntityTypeBuilder<DecisionDevolucionEnvio> e)
    {
        e.ToTable("DecisionesDevolucionEnvio", t =>
            t.HasCheckConstraint("CK_DecisionesDevolucionEnvio_Values",
                "\"VersionAnterior\" >= 0 AND \"VersionNueva\" = \"VersionAnterior\" + 1 AND char_length(btrim(\"Motivo\")) BETWEEN 1 AND 500 AND \"OrganizacionId\" = \"OperadorId\""));
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.Property(x => x.Motivo).HasMaxLength(500).IsRequired();
        e.Property(x => x.DecididaEnUtc).HasColumnType("timestamp with time zone");
        e.HasAlternateKey(x => new { x.OperadorId, x.Id });
        e.HasIndex(x => new { x.OperadorId, x.ComandoId }).IsUnique();
        e.HasIndex(x => new { x.OperadorId, x.EnvioId, x.DecididaEnUtc });
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<VersionReglasOperativas>().WithMany().HasForeignKey(x => new { x.OperadorId, x.VersionReglasId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
