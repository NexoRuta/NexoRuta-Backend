using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.UseCases;

public sealed class EditarBorradorTarifarioUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    ITarifariosRepository repository)
{
    public async Task<VersionTarifario> EjecutarAsync(long versionEsperada,
        IReadOnlyCollection<TarifaZonaModalidadConfiguracion> configuracion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.EditarBorradorAsync(actor.OperadorId!.Value, actor.UsuarioId,
            versionEsperada, configuracion, cancellationToken);
    }
}
