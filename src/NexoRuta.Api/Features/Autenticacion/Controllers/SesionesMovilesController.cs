using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexoRuta.Api.Contracts.Auth;
using NexoRuta.Api.Core.Security;
using NexoRuta.Api.Features.Autenticacion.Contracts;
using NexoRuta.Application.Administracion.Results;
using NexoRuta.Application.Administracion.UseCases;

namespace NexoRuta.Api.Features.Autenticacion.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/auth/mobile")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status429TooManyRequests)]
public sealed class SesionesMovilesController(SesionesMovilesUseCase sesiones,
    JwtTokenService jwt, IConfiguration configuration) : ControllerBase
{
    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    [ProducesResponseType<SesionMovilResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct)
    {
        if (!int.TryParse(configuration["SesionesMoviles:VigenciaRenovacionDias"] ?? "30", out var dias)
            || dias is <= 0 or > 365)
            return Problem("La vigencia de las sesiones móviles no es válida.", statusCode: 503);
        var resultado = await sesiones.IniciarAsync(request.Email, request.Password, TimeSpan.FromDays(dias), ct);
        return resultado is null ? Unauthorized() : Ok(Respuesta(resultado));
    }

    [HttpPost("refresh")]
    [EnableRateLimiting("auth-refresh")]
    [ProducesResponseType<SesionMovilResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Renovar(RenovacionSesionRequest request, CancellationToken ct)
    {
        var resultado = await sesiones.RenovarAsync(request.RefreshToken, ct);
        return resultado is null ? Unauthorized() : Ok(Respuesta(resultado));
    }

    [HttpPost("logout")]
    [EnableRateLimiting("auth-refresh")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revocar(RenovacionSesionRequest request, CancellationToken ct)
    {
        await sesiones.RevocarAsync(request.RefreshToken, ct);
        return NoContent();
    }

    private SesionMovilResponse Respuesta(SesionMovilIniciada resultado)
    {
        var (token, expira) = jwt.Generar(resultado.Identidad, resultado.Sesion.Id, resultado.Sesion.ExpiraEnUtc);
        return new() { AccessToken = token, ExpiresAtUtc = expira, RefreshToken = resultado.TokenRenovacion,
            RefreshExpiresAtUtc = resultado.Sesion.ExpiraEnUtc, SesionId = resultado.Sesion.Id };
    }
}
