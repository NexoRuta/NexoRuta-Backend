using NexoRuta.Domain.Tarifas;
namespace NexoRuta.Application.Envios.Repositories;
public sealed record ServicioEnvioDisponible(Guid ZonaId, string ZonaNombre, string Modalidad);
public sealed record TarifaCargoRegistrado(Guid Id, DateTimeOffset AplicadoEnUtc, ResultadoCalculoTarifa Calculo,
    NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado Servicio, long VersionZona, int RevisionZona);
public interface IServiciosEnvioRepository
{
    Task<IReadOnlyList<ServicioEnvioDisponible>> ListarAsync(Guid operador, double latitud, double longitud, CancellationToken ct);
    Task<ResultadoCalculoTarifa> CotizarAsync(Guid operador, NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado servicio, IReadOnlyList<NexoRuta.Application.Envios.Commands.BultoDeclarado> bultos, CancellationToken ct);
    Task<IReadOnlyList<TarifaCargoRegistrado>> HistorialAsync(Guid envio, CancellationToken ct);
    Task<ResultadoCalculoTarifa?> ObtenerCargoAsync(Guid envio, CancellationToken ct);
}
