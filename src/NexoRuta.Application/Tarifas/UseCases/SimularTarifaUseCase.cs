using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.ZonasCobertura.UseCases;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.UseCases;

public sealed record SolicitudSimulacionTarifario(
    Guid VersionTarifarioId,
    Guid ZonaId,
    ModalidadTarifaria Modalidad,
    IReadOnlyCollection<BultoCalculo> Bultos);

public sealed class SimularTarifaUseCase(
    RequerirOperadorCoberturaUseCase requerirOperador,
    ITarifariosRepository repository)
{
    public async Task<ResultadoCalculoTarifa> EjecutarAsync(SolicitudSimulacionTarifario solicitud,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        if (solicitud.VersionTarifarioId == Guid.Empty || solicitud.ZonaId == Guid.Empty)
            throw new ArgumentException("Versión y zona son obligatorias.", nameof(solicitud));
        if (!Enum.IsDefined(solicitud.Modalidad)) throw new ArgumentOutOfRangeException(nameof(solicitud.Modalidad));
        ArgumentNullException.ThrowIfNull(solicitud.Bultos);
        var actor = await requerirOperador.EjecutarAsync(cancellationToken);
        var selection = await repository.ObtenerParaSimularAsync(actor.OperadorId!.Value,
            solicitud.VersionTarifarioId, solicitud.ZonaId, solicitud.Modalidad, cancellationToken);
        if (selection is null) throw new KeyNotFoundException("La versión no existe en el tarifario del operador.");
        if (!selection.ZonaActiva)
            throw new ConflictoTarifarioException(ReglasTarifario.Errors.ZoneInactive,
                "La zona está inactiva y no se puede ofrecer como servicio.");
        if (selection.Tarifa is null)
            throw new TarifaNoConfiguradaException(solicitud.ZonaId, solicitud.Modalidad);
        return CalculadoraTarifas.Calcular(selection.Tarifa.ACalculo(selection.Version), solicitud.Bultos);
    }
}
