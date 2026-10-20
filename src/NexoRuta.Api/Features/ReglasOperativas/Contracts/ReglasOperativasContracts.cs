using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using NexoRuta.Application.ReglasOperativas.Repositories;
using NexoRuta.Domain.ReglasOperativas;
using ReglasOperativasDomain = NexoRuta.Domain.ReglasOperativas.ReglasOperativas;

namespace NexoRuta.Api.Features.ReglasOperativas.Contracts;

public static class ReglasOperativasApiContract
{
    public const string BasePath = "api/configuracion/reglas-operativas";
}

public static class ReglasOperativasApiLimits
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;
    public const int MaxMotives = 100;
    public const int MaxRequirements = 4;
    public const int MaxRequestBytes = 65_536;
}

public static class ReglasOperativasApiErrors
{
    public const string DraftNotFound = "operational_rules.draft_not_found";
    public const string PublicationNotFound = "operational_rules.publication_not_found";
    public const string InvalidRequest = "operational_rules.invalid_request";
    public const string InvalidConfiguration = "operational_rules.invalid_configuration";
}

public sealed class TipoPruebaEntregaJsonConverter()
    : JsonStringEnumConverter<TipoPruebaEntregaContract>(allowIntegerValues: false) { }

public sealed class PoliticaDevolucionJsonConverter()
    : JsonStringEnumConverter<PoliticaDevolucionContract>(allowIntegerValues: false) { }

public sealed class EstadoVersionReglasOperativasJsonConverter()
    : JsonStringEnumConverter<EstadoVersionReglasOperativasContract>(allowIntegerValues: false) { }

[JsonConverter(typeof(TipoPruebaEntregaJsonConverter))]
public enum TipoPruebaEntregaContract { FirmaEnPantalla, Fotografia, NombreReceptor, DocumentoReceptor }

[JsonConverter(typeof(PoliticaDevolucionJsonConverter))]
public enum PoliticaDevolucionContract { DecisionOperativa, IniciarAutomaticamente }

[JsonConverter(typeof(EstadoVersionReglasOperativasJsonConverter))]
public enum EstadoVersionReglasOperativasContract { Borrador, Publicada }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ReemplazarBorradorReglasOperativasRequest
{
    [JsonRequired, Range(0, long.MaxValue)]
    public long VersionConcurrencia { get; init; }

    [JsonRequired, Range(1, ReglasOperativasDomain.MaxDeliveryAttempts)]
    public int? MaxIntentosEntrega { get; init; }

    [JsonRequired, Range(1, ReglasOperativasDomain.MaxDurationMinutes)]
    public int? IntervaloEntreIntentosMinutos { get; init; }

    [JsonRequired]
    public PoliticaDevolucionContract? PoliticaDevolucionAlAgotarIntentos { get; init; }

    [JsonRequired, Range(1, ReglasOperativasDomain.MaxDurationMinutes)]
    public int? PlazoEstandarMinutos { get; init; }

    [JsonRequired, Range(1, ReglasOperativasDomain.MaxDurationMinutes)]
    public int? PlazoUrgenteMinutos { get; init; }

    [JsonRequired, Required, MaxLength(ReglasOperativasApiLimits.MaxRequirements)]
    public TipoPruebaEntregaContract[] RequisitosPrueba { get; init; } = [];

    [JsonRequired, Required, MaxLength(ReglasOperativasApiLimits.MaxMotives)]
    public MotivoNoEntregaRequest[] Motivos { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PublicarBorradorReglasOperativasRequest
{
    [JsonRequired, Range(0, long.MaxValue)]
    public long VersionConcurrencia { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MotivoNoEntregaRequest
{
    [JsonRequired, Required, StringLength(ReglasOperativasDomain.MotiveCodeMaxLength, MinimumLength = 1)]
    [RegularExpression("^[A-Z][A-Z0-9_]*$")]
    public string Codigo { get; init; } = "";

    [JsonRequired, Required, StringLength(ReglasOperativasDomain.MotiveDescriptionMaxLength, MinimumLength = 1)]
    public string Descripcion { get; init; } = "";

    [JsonRequired]
    public bool Disponible { get; init; }
}

public sealed record ReglasOperativasErrorResponse(
    string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null);

public sealed record ReglasOperativasEstadoResponse(
    bool Configurado,
    VersionReglasOperativasResumenResponse? PublicacionVigente,
    VersionReglasOperativasResumenResponse? Borrador);

public sealed record VersionReglasOperativasResumenResponse(
    Guid VersionReglasOperativasId,
    EstadoVersionReglasOperativasContract Estado,
    int? NumeroVersionPublicada,
    long VersionConcurrencia,
    DateTimeOffset CreadaUtc,
    DateTimeOffset? PublicadaUtc);

public sealed record PaginaPublicacionesReglasOperativasResponse(
    IReadOnlyList<VersionReglasOperativasResumenResponse> Items,
    int Pagina,
    int TamanoPagina,
    int Total);

public sealed record VersionReglasOperativasResponse(
    Guid VersionReglasOperativasId,
    EstadoVersionReglasOperativasContract Estado,
    int? NumeroVersionPublicada,
    long VersionConcurrencia,
    int? MaxIntentosEntrega,
    int? IntervaloEntreIntentosMinutos,
    PoliticaDevolucionContract? PoliticaDevolucionAlAgotarIntentos,
    int? PlazoEstandarMinutos,
    int? PlazoUrgenteMinutos,
    IReadOnlyList<TipoPruebaEntregaContract> RequisitosPrueba,
    IReadOnlyList<MotivoNoEntregaResponse> Motivos,
    DateTimeOffset CreadaUtc,
    DateTimeOffset? PublicadaUtc);

public sealed record MotivoNoEntregaResponse(string Codigo, string Descripcion, bool Disponible);

public static class ReglasOperativasContractMapper
{
    public static ReglasOperativasEstadoResponse Estado(ReglasOperativasConsulta query)
        => new(query.Operador is not null,
            query.PublicacionVigente is null ? null : Resumen(query.PublicacionVigente),
            query.Borrador is null ? null : Resumen(query.Borrador));

    public static VersionReglasOperativasResumenResponse Resumen(VersionReglasOperativas version)
        => new(version.Id, Estado(version.Estado), version.NumeroVersionPublicada,
            version.VersionConcurrencia, version.CreadaUtc, version.PublicadaUtc);

    public static VersionReglasOperativasResponse Detalle(VersionReglasOperativas version)
        => new(version.Id, Estado(version.Estado), version.NumeroVersionPublicada,
            version.VersionConcurrencia, version.MaxIntentosEntrega,
            version.IntervaloEntreIntentosMinutos, Politica(version.PoliticaDevolucionAlAgotarIntentos),
            version.PlazoEstandarMinutos, version.PlazoUrgenteMinutos,
            version.RequisitosPrueba.OrderBy(item => item.Tipo).Select(item => Tipo(item.Tipo)).ToArray(),
            version.Motivos.OrderBy(item => item.Codigo, StringComparer.Ordinal)
                .Select(item => new MotivoNoEntregaResponse(item.Codigo, item.Descripcion, item.Disponible)).ToArray(),
            version.CreadaUtc, version.PublicadaUtc);

    public static ReglasOperativasConfiguracion Configuracion(ReemplazarBorradorReglasOperativasRequest request)
        => new(request.MaxIntentosEntrega, request.IntervaloEntreIntentosMinutos,
            Politica(request.PoliticaDevolucionAlAgotarIntentos), request.PlazoEstandarMinutos,
            request.PlazoUrgenteMinutos, request.RequisitosPrueba.Select(Tipo).ToArray(),
            request.Motivos.Select(item => new MotivoNoEntregaConfiguracion(
                item.Codigo, item.Descripcion, item.Disponible)).ToArray());

    public static string CampoApi(string? field) => field switch
    {
        nameof(VersionReglasOperativas.MaxIntentosEntrega) => "maxIntentosEntrega",
        nameof(VersionReglasOperativas.IntervaloEntreIntentosMinutos) => "intervaloEntreIntentosMinutos",
        nameof(VersionReglasOperativas.PoliticaDevolucionAlAgotarIntentos) => "politicaDevolucionAlAgotarIntentos",
        nameof(VersionReglasOperativas.PlazoEstandarMinutos) => "plazoEstandarMinutos",
        nameof(VersionReglasOperativas.PlazoUrgenteMinutos) => "plazoUrgenteMinutos",
        nameof(VersionReglasOperativas.RequisitosPrueba) => "requisitosPrueba",
        nameof(VersionReglasOperativas.Motivos) => "motivos",
        nameof(ReglasOperativasConfiguracion) => "configuracion",
        _ => "configuracion"
    };

    public static EstadoVersionReglasOperativasContract Estado(EstadoVersionReglasOperativas estado) => estado switch
    {
        EstadoVersionReglasOperativas.Borrador => EstadoVersionReglasOperativasContract.Borrador,
        EstadoVersionReglasOperativas.Publicada => EstadoVersionReglasOperativasContract.Publicada,
        _ => throw new ArgumentOutOfRangeException(nameof(estado))
    };

    private static TipoPruebaEntregaContract Tipo(TipoPruebaEntrega value) => value switch
    {
        TipoPruebaEntrega.FirmaEnPantalla => TipoPruebaEntregaContract.FirmaEnPantalla,
        TipoPruebaEntrega.Fotografia => TipoPruebaEntregaContract.Fotografia,
        TipoPruebaEntrega.NombreReceptor => TipoPruebaEntregaContract.NombreReceptor,
        TipoPruebaEntrega.DocumentoReceptor => TipoPruebaEntregaContract.DocumentoReceptor,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static TipoPruebaEntrega Tipo(TipoPruebaEntregaContract value) => value switch
    {
        TipoPruebaEntregaContract.FirmaEnPantalla => TipoPruebaEntrega.FirmaEnPantalla,
        TipoPruebaEntregaContract.Fotografia => TipoPruebaEntrega.Fotografia,
        TipoPruebaEntregaContract.NombreReceptor => TipoPruebaEntrega.NombreReceptor,
        TipoPruebaEntregaContract.DocumentoReceptor => TipoPruebaEntrega.DocumentoReceptor,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static PoliticaDevolucionContract? Politica(PoliticaDevolucionAlAgotarIntentos? value) => value switch
    {
        null => null,
        PoliticaDevolucionAlAgotarIntentos.DecisionOperativa => PoliticaDevolucionContract.DecisionOperativa,
        PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente => PoliticaDevolucionContract.IniciarAutomaticamente,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static PoliticaDevolucionAlAgotarIntentos? Politica(PoliticaDevolucionContract? value) => value switch
    {
        null => null,
        PoliticaDevolucionContract.DecisionOperativa => PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,
        PoliticaDevolucionContract.IniciarAutomaticamente => PoliticaDevolucionAlAgotarIntentos.IniciarAutomaticamente,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
