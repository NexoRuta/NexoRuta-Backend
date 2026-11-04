using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ConfigurarPerfilVialRequest([Required,Range(0,long.MaxValue)]long? Version,[Required,StringLength(30),RegularExpression("^(SinDeterminar|DriveConfirmado|NoCompatible)$")]string Compatibilidad,bool ConfirmarCompatibilidad,[Required,StringLength(500)]string Motivo);
public sealed record PerfilVialResponse(Guid VehiculoId,string Matricula,long Version,string Compatibilidad,Guid? ActorId,DateTimeOffset? ConfirmadoEnUtc,string? Motivo)
{public static PerfilVialResponse Desde(PerfilVialDatos p)=>new(p.VehiculoId,p.Matricula,p.Version,p.Compatibilidad,p.ActorId,p.ConfirmadoEnUtc,p.Motivo);}
public sealed record CalcularEstimacionRequest(Guid ComandoId,[Required,Range(0,long.MaxValue)]long? VersionRuta);
public sealed record ActualizarEstimacionRequest(Guid ComandoId,[Required,Range(0,long.MaxValue)]long? VersionRuta,Guid OcupacionId);
public sealed record CriteriosGpsResponse(int AntiguedadMaximaSegundos,double PrecisionMaximaMetros);
public sealed record PosicionFuenteResponse(Guid Id,DateTimeOffset MedidaEnUtc,DateTimeOffset RecibidaEnUtc,double PrecisionMetros);
public sealed record HorarioVialResponse(Guid EnvioId,DateTimeOffset LlegadaManualEnUtc,DateTimeOffset LlegadaCalculadaEnUtc,DateTimeOffset SalidaCalculadaEnUtc);
public sealed record TramoVialResponse(int Indice,double DuracionSegundos,double DistanciaMetros,string Proveedor,string Modelo,string Fuente,string Trafico,string Atribucion,DateTimeOffset CalculadoEnUtc,DateTimeOffset? ValidoHastaUtc);
public sealed record EstimacionRutaResponse(Guid RutaId,Guid ComandoId,long NumeroVersion,long VersionRuta,Guid ActorId,DateTimeOffset RegistradaEnUtc,string Estado,string Motivo,string Vigencia,string ZonaHoraria,DateTimeOffset? CalculadoEnUtc,DateTimeOffset? ValidoHastaUtc,DateTimeOffset? RegresoEstimadoBaseEnUtc,DateTimeOffset? ListoPrevistoEnUtc,IReadOnlyList<HorarioVialResponse> Paradas,IReadOnlyList<TramoVialResponse> Tramos,string TipoCalculo,Guid? OcupacionId,PosicionFuenteResponse? PosicionFuente,CriteriosGpsResponse? CriteriosGps,bool EsReferencia,string? MotivoInvalidacion)
{
    public static EstimacionRutaResponse Desde(EstimacionRutaDatos d)
    {var r=d.Resultado;return new(d.RutaId,d.ComandoId,d.NumeroVersion,d.VersionRuta,d.ActorId,d.RegistradaEnUtc,r.Estado.ToString(),d.Motivo,d.Vigencia,r.ZonaHoraria,r.CalculadoEnUtc,r.ValidoHastaUtc,r.RegresoEstimadoBaseEnUtc,r.ListoPrevistoEnUtc,r.Paradas.Select(p=>new HorarioVialResponse(p.EnvioId,p.LlegadaManualEnUtc,p.LlegadaCalculadaEnUtc,p.SalidaCalculadaEnUtc)).ToArray(),r.Tramos.Select(t=>new TramoVialResponse(t.Solicitud.Indice,t.DuracionSegundos,t.DistanciaMetros,t.Procedencia.Proveedor,t.Procedencia.Modelo,t.Procedencia.Fuente.ToString(),t.Procedencia.Trafico.ToString(),t.Procedencia.Atribucion,t.Procedencia.CalculadoEnUtc,t.Procedencia.ValidoHastaUtc)).ToArray(),d.TipoCalculo,d.OcupacionId,d.PosicionFuente is {} gps?new(gps.Id,gps.MedidaEnUtc,gps.RecibidaEnUtc,gps.PrecisionMetros):null,d.CriteriosGps is {} criterios?new(criterios.AntiguedadMaximaSegundos,criterios.PrecisionMaximaMetros):null,d.EsReferencia,d.MotivoInvalidacion);}
}
