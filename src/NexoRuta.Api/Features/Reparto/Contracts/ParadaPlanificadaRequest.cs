namespace NexoRuta.Api.Features.Reparto.Contracts;
public sealed record ParadaPlanificadaRequest(Guid EnvioId, DateTimeOffset LlegadaEstimadaUtc, [System.ComponentModel.DataAnnotations.Range(0, int.MaxValue)] int? ServicioMinutos = null);
