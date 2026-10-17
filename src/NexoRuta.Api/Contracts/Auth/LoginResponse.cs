
namespace NexoRuta.Api.Contracts.Auth;

public sealed record LoginResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresAtUtc,
    string Contexto,
    string? TipoOrganizacion);
