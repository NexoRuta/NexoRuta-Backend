using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Application.ReglasOperativas.UseCases;

public sealed class EditarBorradorReglasOperativasUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    IReglasOperativasRepository repository)
{
    public async Task<VersionReglasOperativas> EjecutarAsync(long versionEsperada,
        ReglasOperativasConfiguracion configuracion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.EditarBorradorAsync(actor.OperadorId!.Value, actor.UsuarioId,
            versionEsperada, configuracion, cancellationToken);
    }
}
