using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Application.Tarifas.Repositories;

public interface ITarifariosRepository
{
    Task<TarifarioConsulta> ConsultarAsync(Guid operadorId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<VersionTarifario> Items, int Total)> ListarPublicacionesAsync(Guid operadorId,
        int pagina, int tamanoPagina, CancellationToken cancellationToken = default);
    Task<TarifarioPublicacionSnapshot?> ObtenerPublicacionAsync(Guid operadorId, Guid versionId,
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
public sealed record DatosSimulacionTarifario(TarifaCalculo? Tarifa, bool ZonaActiva);

public sealed record TarifarioPublicacionSnapshot(
    Guid OperadorId,
    Guid VersionTarifarioId,
    int NumeroVersionPublicada,
    long VersionConcurrencia,
    MonedaTarifaria Moneda,
    DateTimeOffset CreadaUtc,
    DateTimeOffset? PublicadaUtc,
    IReadOnlyList<TarifaPublicadaSnapshot> Tarifas)
{
    public bool EsValido(Guid operadorId, Guid versionId, int? numeroVersion, long versionConcurrencia)
        => OperadorId == operadorId && VersionTarifarioId == versionId
            && NumeroVersionPublicada == numeroVersion && NumeroVersionPublicada > 0
            && VersionConcurrencia == versionConcurrencia && VersionConcurrencia >= 0
            && Enum.IsDefined(Moneda) && Tarifas is not null
            && Tarifas.All(tarifa => tarifa is not null && tarifa.ZonaId != Guid.Empty
                && Enum.IsDefined(tarifa.Modalidad) && tarifa.Ajustes is not null
                && tarifa.Ajustes.All(ajuste => ajuste is not null && ajuste.Id != Guid.Empty
                    && !string.IsNullOrWhiteSpace(ajuste.Nombre) && Enum.IsDefined(ajuste.Tipo)
                    && (ajuste.Tipo is TipoAjusteTarifario.RecargoPorcentual or TipoAjusteTarifario.BonificacionPorcentual
                        ? ajuste.ImporteFijoUyu is null && ajuste.Porcentaje is not null
                        : ajuste.ImporteFijoUyu is not null && ajuste.Porcentaje is null)))
            && Tarifas.Select(tarifa => (tarifa.ZonaId, tarifa.Modalidad)).Distinct().Count() == Tarifas.Count
            && Tarifas.All(tarifa => tarifa.Ajustes.Select(ajuste => ajuste.Id).Distinct().Count() == tarifa.Ajustes.Count);

    public TarifaCalculo? BuscarCalculo(Guid zonaId, ModalidadTarifaria modalidad)
    {
        var tarifa = Tarifas.SingleOrDefault(item => item.ZonaId == zonaId && item.Modalidad == modalidad);
        return tarifa?.ACalculo(this);
    }
}

public sealed record TarifaPublicadaSnapshot(
    Guid ZonaId,
    ModalidadTarifaria Modalidad,
    decimal ImporteBaseUyu,
    decimal PrecioPorKilogramoUyu,
    decimal PrecioPorMetroCubicoUyu,
    IReadOnlyList<AjustePublicadoSnapshot> Ajustes)
{
    public TarifaCalculo ACalculo(TarifarioPublicacionSnapshot version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new(ZonaId, Modalidad, ImporteBaseUyu, PrecioPorKilogramoUyu,
            PrecioPorMetroCubicoUyu,
            Ajustes.OrderBy(adjustment => adjustment.Id)
                .Select(adjustment => new AjusteCalculo(adjustment.Nombre, adjustment.Tipo,
                    adjustment.ImporteFijoUyu ?? adjustment.Porcentaje
                    ?? throw new InvalidOperationException("El ajuste publicado no tiene valor.")))
                .ToArray(), version.VersionTarifarioId, version.NumeroVersionPublicada,
            version.VersionConcurrencia, version.Moneda);
    }
}

public sealed record AjustePublicadoSnapshot(
    Guid Id,
    string Nombre,
    TipoAjusteTarifario Tipo,
    decimal? ImporteFijoUyu,
    decimal? Porcentaje);
