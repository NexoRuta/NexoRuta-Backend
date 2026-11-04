namespace NexoRuta.Application.Reparto.Dtos;
public sealed record CotejoBultoDatos(Guid ComandoId, long VersionEnvio, long VersionBulto, bool Conforme,
    decimal? PesoObservado, decimal? LargoObservado, decimal? AnchoObservado, decimal? AltoObservado, string? Observaciones);
public sealed record MedidasRecepcionDatos(decimal PesoGramos, decimal LargoCentimetros, decimal AnchoCentimetros, decimal AltoCentimetros);
public sealed record HechoRecepcionDatos(Guid ComandoId, Guid BultoId, Guid UsuarioId, Guid AccesoId,
    DateTimeOffset RegistradaEnUtc, MedidasRecepcionDatos Declarado, CotejoBultoDatos Cotejo);
public sealed record BultoDepositoDatos(Guid Id, string Codigo, long Version, string Custodia,
    MedidasRecepcionDatos Declarado, string CotejoActual);
public sealed record ConsultaRecepcionDatos(Guid EnvioId, string EstadoEnvio, long VersionEnvio, string Destinatario,
    Guid BultoSeleccionadoId, IReadOnlyList<BultoDepositoDatos> Bultos, IReadOnlyList<HechoRecepcionDatos> Historial);
public sealed record ResultadoRecepcionDatos(bool Repetida, ConsultaRecepcionDatos Recepcion);
