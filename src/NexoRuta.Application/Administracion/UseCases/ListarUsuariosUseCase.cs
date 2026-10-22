
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;

namespace NexoRuta.Application.Administracion.UseCases;

public sealed class ListarUsuariosUseCase(
    IUsuarioActual usuarioActual,
    IUsuariosOrganizacionRepository usuarios)
{
    public async Task<IReadOnlyList<UsuarioOrganizacionResumen>>
        EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await usuarioActual.ObtenerAsync(
            cancellationToken);

        if (contexto.OperadorId.HasValue ==
            contexto.ComercioId.HasValue)
        {
            throw new InvalidOperationException(
                "El acceso no tiene una organización válida.");
        }

        return await usuarios.ListarAsync(
            contexto.OperadorId,
            contexto.ComercioId,
            cancellationToken);
    }
}
