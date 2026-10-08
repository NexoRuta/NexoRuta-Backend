using NexoRuta.Application.Administracion;

namespace NexoRuta.Application.Envios;

public sealed class ListarEnviosUseCase(IEnviosRepository repository, IUsuarioActual usuarioActual)
{
    public async Task<IReadOnlyList<EnvioDetalle>> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await usuarioActual.ObtenerAsync(cancellationToken);

        return await repository.ListarAsync(contexto.OperadorId, contexto.ComercioId, cancellationToken);
    }
}
