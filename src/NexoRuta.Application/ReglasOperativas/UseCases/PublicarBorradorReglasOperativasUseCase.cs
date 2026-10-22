using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Application.ReglasOperativas.UseCases;

public sealed class PublicarBorradorReglasOperativasUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    IReglasOperativasRepository repository)
{
    public async Task<VersionReglasOperativas> EjecutarAsync(long versionEsperada,
        CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.PublicarBorradorAsync(actor.OperadorId!.Value, actor.UsuarioId,
            versionEsperada, cancellationToken);
    }
}
