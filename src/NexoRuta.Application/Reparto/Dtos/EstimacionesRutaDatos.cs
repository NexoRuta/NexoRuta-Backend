using NexoRuta.Application.Reparto.Estimaciones;
namespace NexoRuta.Application.Reparto.Dtos;
public sealed record PerfilVialDatos(Guid VehiculoId,string Matricula,long Version,string Compatibilidad,Guid? ActorId,DateTimeOffset? ConfirmadoEnUtc,string? Motivo);
public sealed record ConfigurarPerfilVialDatos(long Version,string Compatibilidad,bool ConfirmarCompatibilidad,string Motivo);
public sealed record CalcularEstimacionDatos(Guid ComandoId,long VersionRuta,Guid? OcupacionId = null);
public sealed record EstimacionRutaDatos(Guid RutaId,Guid ComandoId,long NumeroVersion,long VersionRuta,Guid ActorId,DateTimeOffset RegistradaEnUtc,EstimacionInicial Resultado,string Motivo,string Vigencia,string TipoCalculo = "Inicial",Guid? OcupacionId = null,PosicionProyeccion? PosicionFuente = null,AdmisionGpsOptions? CriteriosGps = null,bool EsReferencia = false,string? MotivoInvalidacion = null);
