using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;

namespace NexoRuta.Application.Envios.UseCases;

public sealed class ListarEnviosUseCase(IEnviosRepository repository, IUsuarioActual usuarioActual)
{
    public async Task<IReadOnlyList<EnvioDetalle>> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await usuarioActual.ObtenerAsync(cancellationToken);

        return await repository.ListarAsync(contexto.OperadorId, contexto.ComercioId, cancellationToken);
    }

    public async Task<EnvioDetalle?> ObtenerPorIdAsync(Guid envioId, CancellationToken cancellationToken = default)
    {
        var contexto = await usuarioActual.ObtenerAsync(cancellationToken);

        return await repository.ObtenerPorIdAsync(envioId, contexto.OperadorId, contexto.ComercioId, cancellationToken);
    }
}
