using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.UseCases;

public sealed class ConsultarTarifarioUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    ITarifariosRepository repository)
{
    public const int MaxTamanoPagina = 100;

    public async Task<TarifarioConsulta> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.ConsultarAsync(actor.OperadorId!.Value, cancellationToken);
    }

    public async Task<(IReadOnlyList<VersionTarifario> Items, int Total)> ListarPublicacionesAsync(
        int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
    {
        if (pagina < 1) throw new ArgumentOutOfRangeException(nameof(pagina));
        if (tamanoPagina is < 1 or > MaxTamanoPagina) throw new ArgumentOutOfRangeException(nameof(tamanoPagina));
        if ((long)(pagina - 1) * tamanoPagina > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pagina));
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.ListarPublicacionesAsync(actor.OperadorId!.Value, pagina, tamanoPagina,
            cancellationToken);
    }

    public async Task<VersionTarifario?> ObtenerPublicacionAsync(Guid versionId,
        CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.ObtenerPublicacionAsync(actor.OperadorId!.Value, versionId, cancellationToken);
    }
}
