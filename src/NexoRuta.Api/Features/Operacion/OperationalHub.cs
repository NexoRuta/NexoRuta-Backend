using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Infrastructure.Operacion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Api.Features.Operacion;

[Authorize]
public sealed class OperationalHub(IServiceScopeFactory scopes) : Hub
{
    public async IAsyncEnumerable<OperationalSnapshot> Fotografias([EnumeratorCancellation] CancellationToken ct)
    {
        var original = Context.GetHttpContext()?.Items[typeof(ContextoUsuario)] as ContextoUsuario;
        if (original?.OperadorId is null || original.ComercioId is not null) throw new HubException("Operador requerido.");
        if (!long.TryParse(Context.User?.FindFirst(NexoRuta.Api.Core.Security.JwtSesionClaims.VersionCredencial)?.Value, out var credentialVersion))
            throw new HubException("La credencial no es válida.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, Context.ConnectionAborted);
        ct = linked.Token;
        // Every API reads the same PostgreSQL projection. No in-memory pub/sub or sticky
        // session is required to distribute business updates across API instances.
        while (!ct.IsCancellationRequested)
        {
            OperationalSnapshot snapshot;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var provider = scope.ServiceProvider;
                var account = await provider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<NexoRuta.Domain.Administracion.Usuario>>().FindByIdAsync(original.UsuarioId.ToString());
                if (account is null || account.VersionCredencial != credentialVersion)
                    throw new HubException("La credencial cambió. Volvé a ingresar.");
                var current = await provider.GetRequiredService<IAccesosUsuarioRepository>().ObtenerAsync(original.AccesoId, ct);
                if (current is null || current.UsuarioId != original.UsuarioId || current.OperadorId != original.OperadorId
                    || current.ComercioId is not null || current.VersionSesion != original.VersionSesion
                    || current.Estado != NexoRuta.Domain.Administracion.EstadoOrganizacion.Activa)
                    throw new HubException("La sesión cambió. Volvé a ingresar.");
                var db = provider.GetRequiredService<NexoRutaDbContext>();
                await using var tx = await db.BeginOrJoinAsync(ct);
                await db.ApplyAsync(RlsActorContext.From(current), ct);
                if (!await provider.GetRequiredService<IPermisosUsuarioRepository>().TienePermisoAsync(current.UsuarioId, current.AccesoId, "envios.consultar", ct))
                    throw new HubException("El permiso cambió.");
                snapshot = await provider.GetRequiredService<OperationalReadStore>().SnapshotAsync(current.OperadorId!.Value, ct);
                await tx.CommitAsync(ct);
            }
            // A full atomic photograph on subscription/reconnection closes the snapshot/delta
            // race. Repeated revisions replace state; they never append duplicate entities.
            yield return snapshot;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
