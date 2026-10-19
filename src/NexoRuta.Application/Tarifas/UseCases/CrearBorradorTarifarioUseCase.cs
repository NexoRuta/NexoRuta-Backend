using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.UseCases;

public sealed class CrearBorradorTarifarioUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    ITarifariosRepository repository)
{
    public async Task<VersionTarifario> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.CrearBorradorAsync(actor.OperadorId!.Value, actor.UsuarioId, cancellationToken);
    }
}
