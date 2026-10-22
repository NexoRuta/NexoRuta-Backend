
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexoRuta.Api.Contracts.Auth;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Results;

namespace NexoRuta.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    IniciarSesionUseCase iniciarSesion,
    JwtTokenService tokens, IRegistroRepository registro,
    IRecuperacionPassword recuperacion) : ControllerBase
{
    private const string MensajeSolicitudRecuperacion = "Si el correo pertenece a una cuenta, vas a recibir instrucciones para restablecer la contraseña.";

    [HttpPost("register")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-register")]
    [ProducesResponseType<IdentidadSinOrganizacion>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Registrar(RegistroRequest request, CancellationToken ct)
    {
        try { return Created("/api/usuarios/actual", await registro.RegistrarAsync(request.Email, request.Password, ct)); }
        catch (Exception error) when (error is CorreoYaRegistradoException or CredencialesInvalidasException)
        { return ErrorCuentaHttp.Traducir(error); }
        catch (ArgumentException error) { return BadRequest(new { message = error.Message }); }
    }

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
            expiracion,
            sesion.EsPlataforma ? "Plataforma"
                : sesion.SinOrganizacion ? "SinOrganizacion"
                : sesion.OrganizacionSuspendida ? "OrganizacionSuspendida"
                : sesion.TipoOrganizacion?.ToString() ?? "Desconocido",
            sesion.TipoOrganizacion?.ToString()));
    }

    [HttpPost("password-recovery/request")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-password-recovery")]
    [ProducesResponseType<SolicitarRecuperacionPasswordResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<SolicitarRecuperacionPasswordResponse>> SolicitarRecuperacion(
        [FromBody] SolicitarRecuperacionPasswordRequest request, CancellationToken ct)
    {
        await recuperacion.SolicitarAsync(request.Email, ct);
        return Ok(new SolicitarRecuperacionPasswordResponse(MensajeSolicitudRecuperacion));
    }

    [HttpPost("password-recovery/reset")]
    [AllowAnonymous]
    [EnableRateLimiting("auth-password-recovery")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ErrorAuthResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RestablecerPassword([FromBody] RestablecerPasswordRequest request, CancellationToken ct)
    {
        var result = await recuperacion.RestablecerAsync(request.UsuarioId, request.Token, request.NuevaPassword, ct);
        return result switch
        {
            ResultadoRestablecimientoPassword.Correcto => NoContent(),
            ResultadoRestablecimientoPassword.PasswordNoValida => BadRequest(new ErrorAuthResponse(
                "La contraseña nueva no cumple la política de seguridad. Usá al menos 12 caracteres.")),
            _ => BadRequest(new ErrorAuthResponse("El enlace no es válido o venció. Solicitá uno nuevo."))
        };
    }
}
