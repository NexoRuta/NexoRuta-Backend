namespace NexoRuta.Api.Features.Comercios.Contracts.Responses;

public sealed record AlternativaOperadorEnvioResponse(Guid OperadorId, string Nombre, IReadOnlyList<string> Modalidades);
