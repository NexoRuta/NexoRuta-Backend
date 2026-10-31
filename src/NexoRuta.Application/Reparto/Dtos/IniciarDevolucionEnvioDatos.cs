namespace NexoRuta.Application.Reparto.Dtos;

public sealed record IniciarDevolucionEnvioDatos(long VersionEnvio, Guid ComandoId, string Motivo);
public sealed record DecisionDevolucionEnvioDatos(Guid EnvioId, Guid DecisionId, Guid ComandoId,
    string EstadoDecision, long VersionDecision, string Motivo, DateTimeOffset DecididaEnUtc,
    string EstadoActual, long VersionActual, bool Repetida);
public sealed record ConfirmarExtravioEnvioDatos(long VersionEnvio, Guid ComandoId, string Motivo);
public sealed record ConfirmacionExtravioEnvioDatos(Guid EnvioId, Guid ConfirmacionId, Guid ComandoId,
    string EstadoConfirmado, long VersionAnterior, long VersionNueva, string Motivo, DateTimeOffset ConfirmadaEnUtc,
    string EstadoActual, long VersionActual, bool Repetida);
