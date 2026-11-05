using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Plataforma.Contracts;

public sealed record SembrarFixtureCargaRequest
{
    [Required, MinLength(1), MaxLength(5)]
    public IReadOnlyList<CuentaFixtureCargaRequest> Cuentas { get; init; } = [];
}

public sealed record CuentaFixtureCargaRequest
{
    [Required, EmailAddress]
    public string Email { get; init; } = "";

    [Required, MinLength(12)]
    public string Password { get; init; } = "";
}

public sealed record SembrarFixtureCargaResponse(IReadOnlyList<CuentaFixtureCargaResponse> Cuentas);

public sealed record CuentaFixtureCargaResponse(string Email, Guid JornadaId,
    IReadOnlyList<OperacionRepartoDatos> Operaciones);
