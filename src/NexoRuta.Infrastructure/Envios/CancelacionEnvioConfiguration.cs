using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Infrastructure.Envios;

public sealed class CancelacionEnvioConfiguration : IEntityTypeConfiguration<CancelacionEnvio>
{
    public void Configure(EntityTypeBuilder<CancelacionEnvio> e)
    {
        e.ToTable("CancelacionesEnvio", t =>
        {
            t.HasCheckConstraint("CK_CancelacionesEnvio_Version", "\"VersionAnterior\" >= 0 AND \"VersionEnvio\" = \"VersionAnterior\" + 1");
            t.HasCheckConstraint("CK_CancelacionesEnvio_Actor", "(\"AutorizacionPublicaId\" IS NULL AND \"AccesoId\" IS NOT NULL AND \"UsuarioId\" IS NOT NULL AND \"TipoActor\" IS NOT NULL AND \"OrganizacionActorId\" IS NOT NULL) OR (\"AutorizacionPublicaId\" IS NOT NULL AND \"AccesoId\" IS NULL AND \"UsuarioId\" IS NULL AND \"TipoActor\" IS NULL AND \"OrganizacionActorId\" IS NULL)");
        });
        e.HasKey(x => x.Id); e.Property(x => x.Id).ValueGeneratedNever();
        e.HasIndex(x => new { x.OperadorId, x.EnvioId }).IsUnique();
        e.Property(x => x.TipoActor).HasConversion<string>().HasMaxLength(20);
        e.Property(x => x.Motivo).HasMaxLength(500).IsRequired();
        e.Property(x => x.EstadoAnterior).HasMaxLength(40).IsRequired();
        e.HasOne<Envio>().WithMany().HasForeignKey(x => new { x.OperadorId, x.EnvioId })
            .HasPrincipalKey(x => new { x.OperadorId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<AccesoUsuario>().WithMany().HasForeignKey(x => new { x.AccesoId, x.TipoActor, x.OrganizacionActorId })
            .HasPrincipalKey(x => new { x.Id, x.TipoOrganizacion, x.OrganizacionId }).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<NexoRuta.Infrastructure.Seguimiento.Persistence.CodigoCancelacionPublica>().WithMany()
            .HasForeignKey(x => new { x.OperadorId, x.EnvioId, x.AutorizacionPublicaId })
            .HasPrincipalKey(x => new { x.OperadorId, x.EnvioId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
