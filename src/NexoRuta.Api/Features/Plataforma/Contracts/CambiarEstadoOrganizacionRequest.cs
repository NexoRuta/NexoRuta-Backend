namespace NexoRuta.Api.Features.Plataforma.Contracts;

public sealed record CambiarEstadoOrganizacionRequest(string Estado, string Motivo, long Version);
