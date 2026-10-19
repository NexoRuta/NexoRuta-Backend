namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record RecursosPlanificacionResponse(ConfiguracionRepartoContract Politica,
    RepartidorPlanificacionResponse[] Repartidores, VehiculoContract[] Vehiculos);
public sealed record RepartidorPlanificacionResponse(Guid AccesoId, string Nombre);
