using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IAltaPropiaRepository
{
    Task<AltaOrganizacion> CrearPropiaAsync(Guid usuarioId, TipoAccesoUsuario tipo, string nombre, CancellationToken ct);
}
