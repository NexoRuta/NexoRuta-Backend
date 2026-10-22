namespace NexoRuta.Application.Reparto.Dtos;

public sealed record HojaRutaDatos(Guid Id, Guid JornadaId, Guid RepartidorAccesoId, Guid VehiculoId,
    string Matricula, DateOnly Fecha, string Estado, long Version, long UltimaSecuencia,
    ConfiguracionRepartoDatos Politica, IReadOnlyList<ParadaRutaDatos> Paradas,
    IReadOnlyList<IncidenciaCierreDatos> IncidenciasCierre);
