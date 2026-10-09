using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Api.Seguridad;

// ponytail: selección sin credenciales para el monitoreo; reemplazar este esquema con autenticación real.
public sealed class AccesoSeleccionadoHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IAccesosUsuarioRepository accesos) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(AccesoSeleccionado.Cabecera, out var header))
            return AuthenticateResult.NoResult();
        if (!Guid.TryParse(header, out var accesoId))
            return AuthenticateResult.Fail("El acceso seleccionado no es válido.");
        var acceso = await accesos.ObtenerAsync(accesoId, Context.RequestAborted);
        if (acceso is null)
            return AuthenticateResult.Fail("El acceso seleccionado ya no está disponible.");

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, acceso.UsuarioId.ToString()),
            new Claim(ClaimTypes.Name, acceso.UsuarioEmail),
            new Claim(AccesoSeleccionado.ClaimTipoAcceso, acceso.Tipo),
            new Claim(AccesoSeleccionado.ClaimAccesoId, acceso.AccesoId.ToString())
        ], Scheme.Name);
        Context.Items[typeof(ContextoUsuario)] = acceso;
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new { message = "Seleccioná un usuario válido para continuar." });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Response.WriteAsJsonAsync(new { message = "El usuario seleccionado no tiene permiso para esta operación." });
    }
}
