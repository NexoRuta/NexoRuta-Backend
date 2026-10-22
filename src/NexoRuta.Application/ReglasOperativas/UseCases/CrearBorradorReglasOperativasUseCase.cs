using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Application.ReglasOperativas.UseCases;

public sealed class CrearBorradorReglasOperativasUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    IReglasOperativasRepository repository)
{
    public async Task<VersionReglasOperativas> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.CrearBorradorAsync(actor.OperadorId!.Value, actor.UsuarioId, cancellationToken);
    }
}
