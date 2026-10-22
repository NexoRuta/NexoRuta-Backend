namespace NexoRuta.Domain.Administracion;

/// <summary>A complete immutable public-brand configuration snapshot for one operator.</summary>
public sealed class VersionIdentidadVisualOperador
{
    private VersionIdentidadVisualOperador() { }

    public VersionIdentidadVisualOperador(Guid operadorId, int version, string? nombreComercial,
        string? correoPublico, string? telefonoPublico, string? colorPrimario, string? colorSecundario,
        byte[]? logoContenido, string? logoTipoContenido, Guid autorUsuarioId, DateTimeOffset creadoEnUtc)
    {
        OperadorId = operadorId;
        Version = version;
        NombreComercial = nombreComercial;
        CorreoPublico = correoPublico;
        TelefonoPublico = telefonoPublico;
        ColorPrimario = colorPrimario;
        ColorSecundario = colorSecundario;
        LogoContenido = logoContenido;
        LogoTipoContenido = logoTipoContenido;
        AutorUsuarioId = autorUsuarioId;
        CreadoEnUtc = creadoEnUtc;
    }

    public Guid OperadorId { get; private set; }
    public int Version { get; private set; }
    public string? NombreComercial { get; private set; }
    public string? CorreoPublico { get; private set; }
    public string? TelefonoPublico { get; private set; }
    public string? ColorPrimario { get; private set; }
    public string? ColorSecundario { get; private set; }
    public byte[]? LogoContenido { get; private set; }
    public string? LogoTipoContenido { get; private set; }
    public Guid AutorUsuarioId { get; private set; }
    public DateTimeOffset CreadoEnUtc { get; private set; }
}
