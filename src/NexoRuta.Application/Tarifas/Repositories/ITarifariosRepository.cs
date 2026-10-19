using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.Repositories;

public interface ITarifariosRepository
{
    Task<TarifarioConsulta> ConsultarAsync(Guid operadorId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<VersionTarifario> Items, int Total)> ListarPublicacionesAsync(Guid operadorId,
        int pagina, int tamanoPagina, CancellationToken cancellationToken = default);
    Task<VersionTarifario?> ObtenerPublicacionAsync(Guid operadorId, Guid versionId,
        CancellationToken cancellationToken = default);
    Task<VersionTarifario> CrearBorradorAsync(Guid operadorId, Guid usuarioId, CancellationToken cancellationToken = default);
    Task<VersionTarifario> EditarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
        IReadOnlyCollection<TarifaZonaModalidadConfiguracion> configuracion, CancellationToken cancellationToken = default);
    Task<VersionTarifario> PublicarBorradorAsync(Guid operadorId, Guid usuarioId, long versionEsperada,
        CancellationToken cancellationToken = default);
    Task<DatosSimulacionTarifario?> ObtenerParaSimularAsync(Guid operadorId, Guid versionId, Guid zonaId,
        ModalidadTarifaria modalidad, CancellationToken cancellationToken = default);
}

public sealed record TarifarioConsulta(TarifarioOperador? Tarifario, IReadOnlyList<VersionTarifario> Versiones);
public sealed record DatosSimulacionTarifario(VersionTarifario Version, TarifaZonaModalidad? Tarifa, bool ZonaActiva);
