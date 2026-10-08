namespace NexoRuta.Application.Envios;

public sealed class ObtenerContextoDemoUseCase(IEnviosRepository repository)
{
    public Task<DemoComercioContext?> EjecutarAsync(CancellationToken cancellationToken = default)
        => repository.ObtenerContextoDemoAsync(cancellationToken);
}

public sealed class ListarEnviosUseCase(IEnviosRepository repository)
{
    public async Task<IReadOnlyList<EnvioDetalle>> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await repository.ObtenerContextoDemoAsync(cancellationToken)
            ?? throw new InvalidOperationException("No existe el usuario de demo vinculado a un comercio.");

        return await repository.ListarAsync(contexto, cancellationToken);
    }
}
