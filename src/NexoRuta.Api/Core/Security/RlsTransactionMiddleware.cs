using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace NexoRuta.Api.Core.Security;

public sealed class RlsTransactionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        NexoRutaDbContext db,
        IAccesosUsuarioRepository accesses,
        IPlataformaRepository platform,
        IRegistroRepository identities)
    {
        // Login/refresh y los accesos públicos delimitan su propia transacción y contexto.
        if (httpContext.User.Identity?.IsAuthenticated != true
            || httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await next(httpContext);
            return;
        }

        if (httpContext.Items[typeof(ContextoUsuario)] is not ContextoUsuario
            && httpContext.Items[typeof(ContextoPlataforma)] is not ContextoPlataforma
            && httpContext.Items[typeof(ContextoOrganizacionSuspendida)] is not ContextoOrganizacionSuspendida
            && httpContext.Items[typeof(IdentidadSinOrganizacion)] is not IdentidadSinOrganizacion)
        {
            await httpContext.ChallengeAsync();
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(httpContext.RequestAborted);
        var originalBody = httpContext.Response.Body;
        // ponytail: endpoints return finite JSON; spill to disk if streaming is introduced.
        await using var bufferedResponse = new MemoryStream();
        try
        {
            var actor = await RevalidateAsync(httpContext, accesses, platform, identities);
            if (actor is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await httpContext.ChallengeAsync();
                return;
            }
            await db.ApplyAsync(actor, httpContext.RequestAborted);
            httpContext.Response.Body = bufferedResponse;
            await next(httpContext);
            if (httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest)
                await transaction.RollbackAsync(CancellationToken.None);
            else
                await transaction.CommitAsync(httpContext.RequestAborted);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            httpContext.Response.Body = originalBody;
        }

        bufferedResponse.Position = 0;
        await bufferedResponse.CopyToAsync(originalBody, httpContext.RequestAborted);
    }

    private static async Task<RlsActorContext?> RevalidateAsync(
        HttpContext httpContext,
        IAccesosUsuarioRepository accesses,
        IPlataformaRepository platform,
        IRegistroRepository identities)
    {
        var cancellationToken = httpContext.RequestAborted;
        if (httpContext.Items[typeof(ContextoUsuario)] is ContextoUsuario user)
        {
            var current = await accesses.ObtenerAsync(user.AccesoId, cancellationToken);
            if (current is null || current.UsuarioId != user.UsuarioId || current.VersionSesion != user.VersionSesion
                || current.OperadorId != user.OperadorId || current.ComercioId != user.ComercioId
                || current.Estado != NexoRuta.Domain.Administracion.EstadoOrganizacion.Activa)
                return null;
            httpContext.Items[typeof(ContextoUsuario)] = current;
            return RlsActorContext.From(current);
        }

        if (httpContext.Items[typeof(ContextoPlataforma)] is ContextoPlataforma platformContext)
        {
            var current = await platform.ObtenerAccesoAsync(platformContext.AccesoId, cancellationToken);
            if (current is null || current.UsuarioId != platformContext.UsuarioId
                || current.VersionSesion != platformContext.VersionSesion)
                return null;
            httpContext.Items[typeof(ContextoPlataforma)] = current;
            return RlsActorContext.From(current);
        }

        if (httpContext.Items[typeof(ContextoOrganizacionSuspendida)] is ContextoOrganizacionSuspendida suspended)
        {
            var current = await accesses.ObtenerAsync(suspended.AccesoId, cancellationToken);
            if (current is null || current.UsuarioId != suspended.UsuarioId || current.VersionSesion != suspended.VersionSesion
                || current.Estado != NexoRuta.Domain.Administracion.EstadoOrganizacion.Suspendida
                || current.TipoAcceso != suspended.TipoOrganizacion
                || (current.OperadorId ?? current.ComercioId) != suspended.OrganizacionId)
                return null;
            var refreshed = new ContextoOrganizacionSuspendida(current.UsuarioId, current.AccesoId,
                suspended.OrganizacionId, current.TipoAcceso,
                current.OperadorId.HasValue ? current.OperadorNombre! : current.ComercioNombre!, current.VersionSesion);
            httpContext.Items[typeof(ContextoOrganizacionSuspendida)] = refreshed;
            return RlsActorContext.From(refreshed);
        }

        if (httpContext.Items[typeof(IdentidadSinOrganizacion)] is IdentidadSinOrganizacion identity)
        {
            var current = await identities.ObtenerSinOrganizacionAsync(identity.UsuarioId, cancellationToken);
            if (current is null || current.UsuarioId != identity.UsuarioId)
                return null;
            httpContext.Items[typeof(IdentidadSinOrganizacion)] = current;
            return RlsActorContext.From(current);
        }

        return null;
    }
}
