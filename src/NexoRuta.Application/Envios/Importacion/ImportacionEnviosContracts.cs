using NexoRuta.Application.Envios.Commands;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios.Importacion;

public sealed record SolicitudEnvioImportado(
    int Registro,
    string ReferenciaExterna,
    CrearEnvioCommand Datos,
    short VersionHuella,
    byte[] Huella);

public sealed record EnvioImportadoParaPersistir(
    Envio Envio,
    Destinatario Destinatario,
    Direccion Direccion,
    Bulto Bulto,
    string ReferenciaExterna,
    short VersionHuella,
    byte[] Huella);

public sealed class IdentidadEnvioImportadoEncontrada
{
    public string ReferenciaExterna { get; set; } = "";
    public Guid EnvioId { get; set; }
    public Guid OperadorId { get; set; }
    public short VersionHuella { get; set; }
    public byte[] Huella { get; set; } = [];
}

public interface IEnviosImportTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}

public enum EstadoFilaImportacionEnvio
{
    Creado,
    YaExistente,
    DuplicadoDe,
    Conflicto,
    Invalido,
    NoProcesado
}

public enum TipoRechazoImportacionEnvios
{
    Validacion,
    Conflicto
}

public sealed record ErrorFilaImportacion(string Codigo, string? Campo, string Mensaje);

public sealed record FilaImportacionEnvio(
    int Registro,
    string? ReferenciaExterna,
    EstadoFilaImportacionEnvio Estado,
    Guid? EnvioId,
    int? MismoQueRegistro,
    IReadOnlyList<ErrorFilaImportacion> Errores);

public sealed record ResultadoImportacionEnvios(
    bool Completada,
    TipoRechazoImportacionEnvios? TipoRechazo,
    IReadOnlyList<FilaImportacionEnvio> Filas);
