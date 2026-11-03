using NexoRuta.Application.Administracion.Dtos;

namespace NexoRuta.Application.Administracion.Repositories;

public interface IConsultaIdentidadVisualOperador
{
    Task<IdentidadVisualOperadorSnapshot?> ObtenerVigenteAsync(Guid operadorId,
        CancellationToken cancellationToken = default);
}
