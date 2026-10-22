using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.ZonasCobertura.Exceptions;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.ZonasCobertura.UseCases;

public sealed class RequerirOperadorCoberturaUseCase(
    IUsuarioActual usuarioActual,
    IPermisosUsuarioRepository permisos)
{
    public async Task<ContextoUsuario> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var actor = await usuarioActual.ObtenerAsync(cancellationToken);
        if (actor.TipoAcceso != TipoAccesoUsuario.Operador
            || actor.OperadorId is not { } operadorId || operadorId == Guid.Empty
            || actor.ComercioId is not null || actor.Estado != EstadoOrganizacion.Activa
            || !await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId,
                CatalogoPermisos.ConfiguracionGestionar, cancellationToken))
            throw new AccesoCoberturaNoPermitidoException();
        return actor;
    }
}
