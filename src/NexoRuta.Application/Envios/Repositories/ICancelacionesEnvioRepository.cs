using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Envios.Results;

namespace NexoRuta.Application.Envios.Repositories;

public interface ICancelacionesEnvioRepository
{
    Task<EnvioCancelado?> CancelarAsync(ContextoUsuario actor, Guid envioId, long version, string motivo, CancellationToken ct);
}
