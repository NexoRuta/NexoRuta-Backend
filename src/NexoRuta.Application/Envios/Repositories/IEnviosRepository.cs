using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios;

public interface IEnviosRepository
{
    Task GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        Bulto bulto,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
        Guid? operadorId,
        Guid? comercioId,
        CancellationToken cancellationToken = default);
}
