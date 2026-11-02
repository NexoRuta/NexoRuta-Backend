using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Reparto;
public sealed class PerfilVialRegistro
{
    public Guid OperadorId{get;set;} public Guid VehiculoId{get;set;} public long Version{get;set;}
    public string Compatibilidad{get;set;}="SinDeterminar";public Guid ActorId{get;set;}public DateTimeOffset ConfirmadoEnUtc{get;set;}public string Motivo{get;set;}="";
}
public sealed class EstimacionRegistro
{
    public Guid OperadorId{get;set;}public Guid RutaId{get;set;}public Guid ComandoId{get;set;}public long NumeroVersion{get;set;}public long VersionRuta{get;set;}
    public Guid ActorId{get;set;}public DateTimeOffset RegistradaEnUtc{get;set;}public string ContextoHash{get;set;}="";public string Entrada{get;set;}="";public string Resultado{get;set;}="";public string Motivo{get;set;}="";
}
public sealed class PerfilVialRegistroConfiguration:IEntityTypeConfiguration<PerfilVialRegistro>
{
    public void Configure(EntityTypeBuilder<PerfilVialRegistro> e)
    {
        e.ToTable("PerfilesVialesVehiculo",t=>t.HasCheckConstraint("CK_PerfilVial_Compatibilidad","\"Compatibilidad\" IN ('SinDeterminar','DriveConfirmado','NoCompatible') AND \"Version\">0"));
        e.HasKey(x=>new{x.OperadorId,x.VehiculoId});e.Property(x=>x.Version).IsConcurrencyToken();e.Property(x=>x.Compatibilidad).HasMaxLength(30);e.Property(x=>x.Motivo).HasMaxLength(500);
        e.HasOne<Vehiculo>().WithMany().HasForeignKey(x=>new{x.OperadorId,x.VehiculoId}).HasPrincipalKey(x=>new{x.OperadorId,x.Id}).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class EstimacionRegistroConfiguration:IEntityTypeConfiguration<EstimacionRegistro>
{
    public void Configure(EntityTypeBuilder<EstimacionRegistro> e)
    {
        e.ToTable("EstimacionesRuta");e.HasKey(x=>new{x.OperadorId,x.RutaId,x.NumeroVersion});e.HasIndex(x=>new{x.OperadorId,x.ComandoId}).IsUnique();
        e.Property(x=>x.ContextoHash).HasMaxLength(64);e.Property(x=>x.Motivo).HasMaxLength(80);e.Property(x=>x.Entrada).HasColumnType("jsonb");e.Property(x=>x.Resultado).HasColumnType("jsonb");
        e.HasOne<HojaRuta>().WithMany().HasForeignKey(x=>new{x.OperadorId,x.RutaId}).HasPrincipalKey(x=>new{x.OperadorId,x.Id}).OnDelete(DeleteBehavior.Restrict);
    }
}
