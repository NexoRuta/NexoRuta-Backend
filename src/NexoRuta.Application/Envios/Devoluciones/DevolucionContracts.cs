using NexoRuta.Application.Administracion.Context;

namespace NexoRuta.Application.Envios.Devoluciones;

public sealed record BultoDevolucionComando(Guid BultoId, long VersionBulto);

public sealed record TransferirDevolucionComando(
    Guid ComandoId,
    long VersionEnvio,
    IReadOnlyList<BultoDevolucionComando> Bultos,
    bool ConfirmarTransferenciaFisica);

public sealed record LineaAcuseDevolucionComando(Guid BultoId, long VersionBulto, string? Observacion);

public sealed record AcusarDevolucionComando(
    Guid ComandoId,
    long VersionTransferencia,
    bool Conforme,
    IReadOnlyList<LineaAcuseDevolucionComando> Bultos);

public sealed record DevolucionResumenDatos(
    Guid EnvioId,
    Guid OperadorId,
    string OperadorNombre,
    string Referencia,
    string EstadoEnvio,
    DateTimeOffset ActividadEnUtc,
    int TotalBultos,
    int EnRetorno,
    int EnDeposito,
    int PendientesAcuse,
    int RecibidosConObservaciones,
    string Situacion);

public sealed record PaginaDevolucionesDatos(IReadOnlyList<DevolucionResumenDatos> Items, string? CursorSiguiente);

public sealed record BultoDevolucionDatos(
    Guid Id,
    string Codigo,
    string Resultado,
    string Custodia,
    long Version,
    Guid? TransferenciaId,
    string Situacion,
    bool PuedeAcusar,
    string? MotivoBloqueo,
    string? ResultadoAcuse,
    string? Observacion,
    DateTimeOffset? TransferidoEnUtc,
    DateTimeOffset? AcusadoEnUtc);

public sealed record DevolucionDetalleDatos(
    Guid EnvioId,
    Guid OperadorId,
    string OperadorNombre,
    string Referencia,
    string EstadoEnvio,
    long VersionEnvio,
    bool EsRetiro,
    int TotalBultos,
    int PendientesAcuse,
    IReadOnlyList<BultoDevolucionDatos> Bultos);

public sealed record HechoDevolucionDatos(
    Guid Id,
    string Tipo,
    DateTimeOffset RegistradoEnUtc,
    DateTimeOffset? OcurridoEnUtc,
    string Actor,
    int? Bultos,
    string? Resultado,
    string? Observaciones);

public sealed record TransferenciaDevolucionResultadoDatos(Guid TransferenciaId, int Bultos, DateTimeOffset RegistradaEnUtc);

public sealed record AcuseDevolucionResultadoDatos(Guid AcuseId, int Bultos, string Resultado, DateTimeOffset RegistradoEnUtc);

public interface IDevolucionesRepository
{
    Task<PaginaDevolucionesDatos> ListarAsync(ContextoUsuario actor, Guid? operadorId,
        DateTimeOffset desdeUtc, DateTimeOffset hastaUtc, string? situacion, string? referencia,
        string? cursor, int limite, CancellationToken cancellationToken);

    Task<DevolucionDetalleDatos?> ObtenerAsync(ContextoUsuario actor, Guid operadorId, Guid envioId,
        int desdeBulto, int limite, bool puedeAcusar, CancellationToken cancellationToken);

    Task<IReadOnlyList<HechoDevolucionDatos>?> HistorialAsync(ContextoUsuario actor, Guid operadorId,
        Guid envioId, CancellationToken cancellationToken);

    Task<TransferenciaDevolucionResultadoDatos?> TransferirAsync(ContextoUsuario actor, Guid envioId,
        TransferirDevolucionComando comando, CancellationToken cancellationToken);

    Task<AcuseDevolucionResultadoDatos?> AcusarAsync(ContextoUsuario actor, Guid operadorId,
        Guid transferenciaId, AcusarDevolucionComando comando, CancellationToken cancellationToken);
}
