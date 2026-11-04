namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ComandoFlotaDatos(Guid ComandoId, Guid? OcupacionId, long Version, string Motivo,
    Guid? EnvioId = null, long? VersionEnvio = null);
public sealed record ResultadoFlotaDatos(Guid ComandoId, Guid VehiculoId, Guid? RutaId, Guid? OcupacionId,
    long Version, string Accion, DateTimeOffset ConfirmadoEnUtc, Guid? EnvioId = null, long? VersionEnvio = null,
    IReadOnlyDictionary<Guid, long>? VersionesBultosAnteriores = null);
public sealed record OcupacionFlotaDatos(Guid Id, Guid RutaId, Guid RepartidorAccesoId, DateOnly Fecha,
    string EstadoRuta, long Version, bool Legada, DateTimeOffset? AdquiridaEnUtc,
    DateTimeOffset? RetornoEnUtc, int CargaPendiente, bool PuedeAnular);
public sealed record VehiculoFlotaDatos(Guid Id, string Matricula, bool Activo, long Version,
    DateTimeOffset? VerificacionInicialEnUtc, int CargaPendiente,
    IReadOnlyList<OcupacionFlotaDatos> Ocupaciones, DateTimeOffset? UltimaLiberacionEnUtc,
    IReadOnlyList<CargaInicialFlotaDatos> CargasIniciales);
public sealed record CargaInicialFlotaDatos(Guid EnvioId, long VersionEnvio, Guid RutaId, DateOnly Fecha, int BultosPendientes);
