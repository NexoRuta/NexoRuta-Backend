namespace NexoRuta.Api.Features.Autenticacion.Contracts;

public sealed class SesionMovilResponse
{
    public required string AccessToken { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public required DateTime ExpiresAtUtc { get; init; }
    public required string RefreshToken { get; init; }
    public required DateTimeOffset RefreshExpiresAtUtc { get; init; }
    public required Guid SesionId { get; init; }
}
