
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexoRuta.Api.Contracts.Auth;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.UseCases;

namespace NexoRuta.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IniciarSesionUseCase iniciarSesion,
    JwtTokenService tokens) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-login")]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var sesion = await iniciarSesion.EjecutarAsync(
            request.Email,
            request.Password,
            cancellationToken);

        if (sesion is null)
            return Unauthorized(new
            {
                message = "Credenciales inválidas."
            });

        var (token, expiracion) = tokens.Generar(sesion);

        return Ok(new LoginResponse(
            token,
            "Bearer",
            expiracion));
    }
}
