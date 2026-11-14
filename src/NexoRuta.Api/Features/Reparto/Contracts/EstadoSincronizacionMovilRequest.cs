namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record EstadoSincronizacionMovilRequest(Guid InstallationId, long Sequence, int PendingOperations);
