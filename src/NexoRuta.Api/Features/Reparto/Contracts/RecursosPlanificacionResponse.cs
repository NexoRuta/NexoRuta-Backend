namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record RecursosPlanificacionResponse(ConfiguracionRepartoContract Politica,
    RepartidorPlanificacionResponse[] Repartidores, VehiculoContract[] Vehiculos, BaseOperativaResponse? BaseOperativa = null);
public sealed record RepartidorPlanificacionResponse(Guid AccesoId, string Nombre);
