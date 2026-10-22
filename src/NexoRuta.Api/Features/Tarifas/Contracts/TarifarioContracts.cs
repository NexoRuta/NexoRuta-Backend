using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NexoRuta.Application.Tarifas.Repositories;
using NexoRuta.Application.Tarifas.UseCases;
using NexoRuta.Domain.Tarifas;

namespace NexoRuta.Api.Features.Tarifas.Contracts;

public static class TarifarioApiLimits
{
    public const int MaxPageSize = ConsultarTarifarioUseCase.MaxTamanoPagina;
    public const int MaxRates = 500;
    public const int MaxAdjustmentsPerRate = 100;
    public const int MaxParcels = 100;
    public const int MaxRequestBytes = 1_048_576;
}

public static class TarifarioApiContract
{
    public const string BasePath = "api/configuracion/tarifas";
}

[JsonConverter(typeof(JsonStringEnumConverter<ModalidadTarifariaContract>))]
public enum ModalidadTarifariaContract { Estandar, Urgente }

[JsonConverter(typeof(JsonStringEnumConverter<TipoAjusteTarifarioContract>))]
public enum TipoAjusteTarifarioContract { RecargoFijo, RecargoPorcentual, BonificacionFija, BonificacionPorcentual }

[JsonConverter(typeof(JsonStringEnumConverter<MonedaTarifariaContract>))]
public enum MonedaTarifariaContract { UYU }

[JsonConverter(typeof(JsonStringEnumConverter<EstadoVersionTarifarioContract>))]
public enum EstadoVersionTarifarioContract { Borrador, Publicada }

[JsonConverter(typeof(JsonStringEnumConverter<BaseAjusteTarifarioContract>))]
public enum BaseAjusteTarifarioContract { ImporteFijo, SubtotalOriginal }

[JsonConverter(typeof(JsonStringEnumConverter<OrigenSimulacionTarifarioContract>))]
public enum OrigenSimulacionTarifarioContract { Borrador, Publicacion }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReemplazarTarifasBorradorRequest
{
    [JsonRequired, Range(0, long.MaxValue)] public long VersionConcurrencia { get; init; }
    [JsonRequired, Required, MaxLength(TarifarioApiLimits.MaxRates)] public TarifaZonaModalidadRequest[] Tarifas { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PublicarBorradorTarifarioRequest
{
    [JsonRequired, Range(0, long.MaxValue)] public long VersionConcurrencia { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TarifaZonaModalidadRequest
{
    [JsonRequired] public Guid ZonaId { get; init; }
    [JsonRequired] public ModalidadTarifariaContract Modalidad { get; init; }
    [JsonRequired] public decimal ImporteBaseUyu { get; init; }
    [JsonRequired] public decimal PrecioPorKilogramoUyu { get; init; }
    [JsonRequired] public decimal PrecioPorMetroCubicoUyu { get; init; }
    [JsonRequired, Required, MaxLength(TarifarioApiLimits.MaxAdjustmentsPerRate)] public AjusteTarifarioRequest[] Ajustes { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AjusteTarifarioRequest
{
    [JsonRequired, Required, StringLength(ReglasTarifario.NombreMaxLength, MinimumLength = 1)] public string Nombre { get; init; } = "";
    [JsonRequired] public TipoAjusteTarifarioContract Tipo { get; init; }
    public decimal? ImporteFijoUyu { get; init; }
    public decimal? Porcentaje { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SimularTarifaRequest
{
    [JsonRequired] public Guid VersionTarifarioId { get; init; }
    [JsonRequired] public Guid ZonaId { get; init; }
    [JsonRequired] public ModalidadTarifariaContract Modalidad { get; init; }
    [JsonRequired, Required, MinLength(1), MaxLength(TarifarioApiLimits.MaxParcels)] public BultoSimulacionRequest[] Bultos { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class BultoSimulacionRequest
{
    [JsonRequired] public decimal PesoGramos { get; init; }
    [JsonRequired] public decimal LargoCentimetros { get; init; }
    [JsonRequired] public decimal AnchoCentimetros { get; init; }
    [JsonRequired] public decimal AltoCentimetros { get; init; }
}

public sealed record TarifarioEstadoResponse(
    bool Configurado,
    VersionTarifarioResumenResponse? PublicacionVigente,
    VersionTarifarioResumenResponse? Borrador);

public sealed record PaginaPublicacionesTarifarioResponse(
    IReadOnlyList<VersionTarifarioResumenResponse> Items, int Pagina, int TamanoPagina, int Total);

public sealed record VersionTarifarioResumenResponse(
    Guid VersionTarifarioId, EstadoVersionTarifarioContract Estado, int? NumeroVersionPublicada,
    long VersionConcurrencia, MonedaTarifariaContract Moneda, DateTimeOffset CreadaUtc,
    DateTimeOffset? PublicadaUtc);

public sealed record VersionTarifarioResponse(
    Guid VersionTarifarioId, EstadoVersionTarifarioContract Estado, int? NumeroVersionPublicada,
    long VersionConcurrencia, MonedaTarifariaContract Moneda, DateTimeOffset CreadaUtc,
    DateTimeOffset? PublicadaUtc, IReadOnlyList<TarifaZonaModalidadResponse> Tarifas);

public sealed record TarifaZonaModalidadResponse(
    Guid ZonaId, ModalidadTarifariaContract Modalidad, decimal ImporteBaseUyu,
    decimal PrecioPorKilogramoUyu, decimal PrecioPorMetroCubicoUyu,
    IReadOnlyList<AjusteTarifarioResponse> Ajustes);

public sealed record AjusteTarifarioResponse(
    string Nombre, TipoAjusteTarifarioContract Tipo, decimal? ImporteFijoUyu, decimal? Porcentaje);

public sealed record SimulacionTarifaResponse(
    OrigenSimulacionTarifarioContract Origen, Guid VersionTarifarioId, int? NumeroVersionPublicada,
    long VersionConcurrencia, Guid ZonaId, ModalidadTarifariaContract Modalidad, MonedaTarifariaContract Moneda,
    decimal PesoTotalKg, decimal VolumenTotalM3, decimal ImporteBaseUyu, decimal ComponentePesoUyu,
    decimal ComponenteVolumenUyu, IReadOnlyList<AjusteTarifarioAplicadoResponse> Ajustes,
    decimal SubtotalUyu, decimal RecargosUyu, decimal BonificacionesUyu, decimal TotalPrevioPisoUyu,
    bool PisoCeroAplicado, decimal TotalFinalUyu);

public sealed record AjusteTarifarioAplicadoResponse(
    string Nombre, TipoAjusteTarifarioContract Tipo, BaseAjusteTarifarioContract Base,
    decimal ValorConfigurado, decimal ImporteUyu);

public static class TarifarioContractMapper
{
    public static TarifarioEstadoResponse Estado(TarifarioConsulta query)
    {
        var currentId = query.Tarifario?.VersionVigenteId;
        var current = currentId is null ? null : query.Versiones.SingleOrDefault(x => x.Id == currentId);
        var draft = query.Versiones.SingleOrDefault(x => x.Estado == EstadoVersionTarifario.Borrador);
        return new(query.Tarifario is not null,
            current is null ? null : Resumen(current),
            draft is null ? null : Resumen(draft));
    }

    public static VersionTarifarioResumenResponse Resumen(VersionTarifario version)
        => new(version.Id, Estado(version.Estado), version.NumeroVersionPublicada, version.VersionConcurrencia,
            Moneda(version.Moneda), version.CreadaUtc, version.PublicadaUtc);

    public static VersionTarifarioResponse Detalle(VersionTarifario version)
        => new(version.Id, Estado(version.Estado), version.NumeroVersionPublicada, version.VersionConcurrencia,
            Moneda(version.Moneda), version.CreadaUtc, version.PublicadaUtc,
            version.Tarifas.OrderBy(x => x.ZonaId).ThenBy(x => x.Modalidad)
                .Select(rate => new TarifaZonaModalidadResponse(rate.ZonaId, Modalidad(rate.Modalidad),
                    rate.ImporteBaseUyu, rate.PrecioPorKilogramoUyu, rate.PrecioPorMetroCubicoUyu,
                    rate.Ajustes.OrderBy(x => x.Id).Select(adjustment => new AjusteTarifarioResponse(
                        adjustment.Nombre, Tipo(adjustment.Tipo), adjustment.ImporteFijoUyu,
                        adjustment.Porcentaje)).ToArray())).ToArray());

    public static SimulacionTarifaResponse Simulacion(ResultadoCalculoTarifa result)
    {
        if (result.VersionTarifarioId is not { } versionId || result.VersionConcurrencia is not { } concurrency)
            throw new InvalidOperationException("La simulación no incluyó el identificador de versión y su concurrencia.");
        return new(result.NumeroVersionPublicada is null ? OrigenSimulacionTarifarioContract.Borrador
                : OrigenSimulacionTarifarioContract.Publicacion,
            versionId, result.NumeroVersionPublicada, concurrency,
            result.ZonaId, Modalidad(result.Modalidad), Moneda(result.Moneda), result.PesoTotalKilogramos,
            result.VolumenTotalMetrosCubicos, result.ImporteBaseUyu, result.ComponentePesoUyu, result.ComponenteVolumenUyu,
            result.Ajustes.Select(x => new AjusteTarifarioAplicadoResponse(x.Nombre, Tipo(x.Tipo), Base(x.Base),
                x.ValorConfigurado, x.ImporteUyu)).ToArray(), result.SubtotalUyu, result.TotalRecargosUyu,
            result.TotalBonificacionesUyu, result.TotalExactoUyu, result.PisoCeroAplicado, result.TotalFinalUyu);
    }

    public static ModalidadTarifaria Modalidad(ModalidadTarifariaContract value) => value switch
    {
        ModalidadTarifariaContract.Estandar => ModalidadTarifaria.Estandar,
        ModalidadTarifariaContract.Urgente => ModalidadTarifaria.Urgente,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    public static TipoAjusteTarifario Tipo(TipoAjusteTarifarioContract value) => value switch
    {
        TipoAjusteTarifarioContract.RecargoFijo => TipoAjusteTarifario.RecargoFijo,
        TipoAjusteTarifarioContract.RecargoPorcentual => TipoAjusteTarifario.RecargoPorcentual,
        TipoAjusteTarifarioContract.BonificacionFija => TipoAjusteTarifario.BonificacionFija,
        TipoAjusteTarifarioContract.BonificacionPorcentual => TipoAjusteTarifario.BonificacionPorcentual,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static EstadoVersionTarifarioContract Estado(EstadoVersionTarifario value) => value switch
    {
        EstadoVersionTarifario.Borrador => EstadoVersionTarifarioContract.Borrador,
        EstadoVersionTarifario.Publicada => EstadoVersionTarifarioContract.Publicada,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static MonedaTarifariaContract Moneda(MonedaTarifaria value) => value switch
    {
        MonedaTarifaria.UYU => MonedaTarifariaContract.UYU,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static ModalidadTarifariaContract Modalidad(ModalidadTarifaria value) => value switch
    {
        ModalidadTarifaria.Estandar => ModalidadTarifariaContract.Estandar,
        ModalidadTarifaria.Urgente => ModalidadTarifariaContract.Urgente,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static TipoAjusteTarifarioContract Tipo(TipoAjusteTarifario value) => value switch
    {
        TipoAjusteTarifario.RecargoFijo => TipoAjusteTarifarioContract.RecargoFijo,
        TipoAjusteTarifario.RecargoPorcentual => TipoAjusteTarifarioContract.RecargoPorcentual,
        TipoAjusteTarifario.BonificacionFija => TipoAjusteTarifarioContract.BonificacionFija,
        TipoAjusteTarifario.BonificacionPorcentual => TipoAjusteTarifarioContract.BonificacionPorcentual,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static BaseAjusteTarifarioContract Base(BaseAjusteTarifario value) => value switch
    {
        BaseAjusteTarifario.ImporteFijo => BaseAjusteTarifarioContract.ImporteFijo,
        BaseAjusteTarifario.SubtotalOriginal => BaseAjusteTarifarioContract.SubtotalOriginal,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

}
