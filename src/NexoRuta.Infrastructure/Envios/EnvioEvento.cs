namespace NexoRuta.Infrastructure.Envios;

public static class TipoEventoEnvio
{
    public const string Creacion = "Creacion";
    public const string TransicionEstado = "TransicionEstado";
    public const string EstadoObservadoInicio = "EstadoObservadoInicio";
    public const string Hecho = "Hecho";
}

public sealed class EnvioEvento
{
    public Guid Id { get; init; }
    public Guid OperadorId { get; init; }
    public Guid ComercioId { get; init; }
    public Guid EnvioId { get; init; }
    public string Tipo { get; init; } = "";
    public string? EstadoAnterior { get; init; }
    public string? EstadoNuevo { get; init; }
    public string? EstadoObservado { get; init; }
    public long? VersionAnterior { get; init; }
    public long VersionNueva { get; init; }
    public Guid VersionReglasId { get; init; }
    public Guid? OperacionJornadaId { get; init; }
    public Guid? CorrelacionId { get; init; }
    public string ActorTipo { get; init; } = "";
    public Guid? ActorUsuarioId { get; init; }
    public Guid? ActorAccesoId { get; init; }
    public Guid? ActorOrganizacionId { get; init; }
    public string? ActorProceso { get; init; }
    public Guid? AutorizacionPublicaId { get; init; }
    public string OrigenTipo { get; init; } = "";
    public Guid? OrigenId { get; init; }
    public string? CodigoHecho { get; init; }
    public string Procedencia { get; init; } = "";
    public DateTimeOffset? OcurridaEnUtc { get; init; }
    public DateTimeOffset RegistradaEnUtc { get; init; }
    public Guid? CancelacionEnvioId { get; init; }
}
