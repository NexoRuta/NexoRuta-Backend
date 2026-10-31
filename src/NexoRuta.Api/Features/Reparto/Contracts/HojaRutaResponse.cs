using NexoRuta.Application.Reparto.Dtos;
namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record HojaRutaResponse(Guid Id, Guid JornadaId, Guid RepartidorAccesoId, Guid VehiculoId,
    string Matricula, DateOnly Fecha, string Estado, long Version, long UltimaSecuencia,
    ConfiguracionRepartoContract Politica, IReadOnlyList<ParadaRutaResponse> Paradas,
    IReadOnlyList<IncidenciaCierreDatos> IncidenciasCierre, bool ReservaRecursosActiva)
{
    public static HojaRutaResponse Desde(HojaRutaDatos x) => new(x.Id, x.JornadaId, x.RepartidorAccesoId,
        x.VehiculoId, x.Matricula, x.Fecha, x.Estado, x.Version, x.UltimaSecuencia,
        ConfiguracionRepartoContract.Desde(x.Politica), x.Paradas.Select(ParadaRutaResponse.Desde).ToArray(), x.IncidenciasCierre,
        x.ReservaRecursosActiva);
}
