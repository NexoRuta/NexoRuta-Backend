using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.UseCases;

public sealed class PublicarBorradorTarifarioUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    ITarifariosRepository repository)
{
    public async Task<VersionTarifario> EjecutarAsync(long versionEsperada,
        CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.PublicarBorradorAsync(actor.OperadorId!.Value, actor.UsuarioId,
            versionEsperada, cancellationToken);
    }
}
