using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.Results;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class SesionesMovilesUseCase(IniciarSesionUseCase login,
    IAccesosUsuarioRepository accesos, ISesionesMovilesRepository sesiones,
    ITokensRenovacion tokens, TimeProvider reloj)
{
    public async Task<SesionMovilIniciada?> IniciarAsync(string email, string password,
        TimeSpan vigencia, CancellationToken ct)
    {
        if (vigencia <= TimeSpan.Zero) throw new ArgumentException("La vigencia debe ser positiva.");
        var identidad = await login.EjecutarAsync(email, password, ct);
        if (identidad is null || identidad.EsPlataforma || identidad.AccesoId is null
            || identidad.TipoOrganizacion != TipoAccesoUsuario.Operador || identidad.OrganizacionSuspendida)
            return null;
        var ahora = reloj.GetUtcNow();
        var sesion = new SesionMovil(identidad.UsuarioId, identidad.AccesoId.Value,
            identidad.VersionSesion, ahora, ahora.Add(vigencia));
        var token = tokens.Crear();
        await sesiones.CrearAsync(sesion, token.Hash, ct);
        return new(identidad, sesion, token.Token);
    }

    public async Task<SesionMovilIniciada?> RenovarAsync(string tokenRenovacion, CancellationToken ct)
    {
        var hash = tokens.CalcularHash(tokenRenovacion);
        if (hash is null) return null;
        var sesion = await sesiones.ObtenerPorTokenAsync(hash, ct);
        if (sesion is null) return null;
        var actual = await accesos.ObtenerAsync(sesion.AccesoId, ct);
        var ahora = reloj.GetUtcNow();
        if (actual is null || actual.UsuarioId != sesion.UsuarioId || actual.VersionSesion != sesion.VersionSesion
            || actual.TipoAcceso != TipoAccesoUsuario.Operador || actual.Estado != EstadoOrganizacion.Activa)
        {
            await sesiones.RevocarAsync(hash, ahora, ct);
            return null;
        }
        var nuevo = tokens.Crear();
        var rotada = await sesiones.RotarAsync(hash, nuevo.Hash, ahora, ct);
        return rotada is null ? null : new(new SesionIniciada(actual.UsuarioId, actual.AccesoId,
            actual.UsuarioEmail, actual.VersionSesion, TipoOrganizacion: actual.TipoAcceso), rotada, nuevo.Token);
    }

    public Task RevocarAsync(string tokenRenovacion, CancellationToken ct)
        => tokens.CalcularHash(tokenRenovacion) is { } hash
            ? sesiones.RevocarAsync(hash, reloj.GetUtcNow(), ct)
            : Task.CompletedTask;
}
