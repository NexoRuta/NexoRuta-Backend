namespace NexoRuta.Application.Reparto.Dtos;

public sealed record PlanificarRutaDatos(Guid RepartidorAccesoId, Guid VehiculoId, DateOnly Fecha,
    IReadOnlyList<ParadaPlanificadaDatos> Paradas);
