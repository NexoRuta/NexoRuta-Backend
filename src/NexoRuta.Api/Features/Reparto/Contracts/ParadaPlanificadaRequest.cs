namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ParadaPlanificadaRequest(Guid EnvioId, DateTimeOffset LlegadaEstimadaUtc);
