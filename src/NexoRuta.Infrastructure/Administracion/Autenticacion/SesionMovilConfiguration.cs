using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class SesionMovilConfiguration : IEntityTypeConfiguration<SesionMovil>
{
    public void Configure(EntityTypeBuilder<SesionMovil> entity)
    {
        entity.ToTable("SesionesMoviles", t =>
        {
            t.HasCheckConstraint("CK_SesionesMoviles_Vigencia", "\"ExpiraEnUtc\" > \"CreadaEnUtc\"");
            t.HasCheckConstraint("CK_SesionesMoviles_Version", "\"VersionSesion\" >= 0");
            t.HasCheckConstraint("CK_SesionesMoviles_VersionCredencial", "\"VersionCredencial\" >= 0");
        });
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.HasIndex(x => x.ExpiraEnUtc);
        entity.HasOne<Usuario>().WithMany().HasForeignKey(x => x.UsuarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<AccesoUsuario>().WithMany().HasForeignKey(x => new { x.AccesoId, x.UsuarioId })
            .HasPrincipalKey(x => new { x.Id, x.UsuarioId }).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class TokenRenovacionMovilConfiguration : IEntityTypeConfiguration<TokenRenovacionMovil>
{
    public void Configure(EntityTypeBuilder<TokenRenovacionMovil> entity)
    {
        entity.ToTable("TokensRenovacionMoviles", t =>
            t.HasCheckConstraint("CK_TokensRenovacionMoviles_Hash", "\"Hash\" ~ '^[0-9A-F]{64}$'"));
        entity.HasKey(x => x.Hash);
        entity.Property(x => x.Hash).HasMaxLength(64).ValueGeneratedNever();
        entity.HasOne<SesionMovil>().WithMany().HasForeignKey(x => x.SesionId).OnDelete(DeleteBehavior.Cascade);
    }
}
