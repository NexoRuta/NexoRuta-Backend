
using Microsoft.AspNetCore.Identity;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class VerificadorPassword(
    UserManager<Usuario> users) : IVerificadorPassword
{
    public async Task<bool> VerificarAsync(Guid usuarioId, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(password))
            return false;

        var user = await users.FindByIdAsync(usuarioId.ToString());
        return user is not null && await users.CheckPasswordAsync(user, password);
    }
}
