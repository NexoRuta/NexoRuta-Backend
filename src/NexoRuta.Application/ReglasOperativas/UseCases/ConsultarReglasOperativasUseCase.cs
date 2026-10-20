using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.ReglasOperativas;

namespace NexoRuta.Application.ReglasOperativas.UseCases;

public sealed class ConsultarReglasOperativasUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    IReglasOperativasRepository repository)
{
    public async Task<ReglasOperativasConsulta> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        return await repository.ConsultarAsync(actor.OperadorId!.Value, cancellationToken);
    }

    public async Task<VersionReglasOperativas?> ObtenerBorradorAsync(CancellationToken cancellationToken = default)
        => (await EjecutarAsync(cancellationToken)).Borrador;

    public async Task<(IReadOnlyList<VersionReglasOperativas> Items, int Total)> ListarPublicacionesAsync(
        int pagina, int tamanoPagina, CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        if (pagina < 1 || tamanoPagina is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pagina));
        return await repository.ListarPublicacionesAsync(actor.OperadorId!.Value, pagina, tamanoPagina, cancellationToken);
    }

    public async Task<VersionReglasOperativas?> ObtenerPublicacionAsync(Guid versionId,
        CancellationToken cancellationToken = default)
    {
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        if (versionId == Guid.Empty) return null;
        return await repository.ObtenerPublicacionAsync(actor.OperadorId!.Value, versionId, cancellationToken);
    }
}
