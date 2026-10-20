namespace NexoRuta.Domain.ReglasOperativas;

public sealed class ReglasOperativasOperador
{
    public Guid OperadorId { get; private set; }
    public Guid? VersionVigenteId { get; private set; }
    public int UltimoNumeroPublicacion { get; private set; }
    public DateTimeOffset ActualizadoUtc { get; private set; }

    private ReglasOperativasOperador() { }

    public static ReglasOperativasOperador Crear(Guid operadorId, DateTimeOffset? ahoraUtc = null)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        return new() { OperadorId = operadorId, ActualizadoUtc = ValidarUtc(ahoraUtc) };
    }

    public void RegistrarEdicion(DateTimeOffset? ahoraUtc = null)
        => ActualizadoUtc = ValidarUtc(ahoraUtc);

    public void Publicar(VersionReglasOperativas version, DateTimeOffset? ahoraUtc = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var siguiente = checked(UltimoNumeroPublicacion + 1);
        if (version.OperadorId != OperadorId
            || version.Estado != EstadoVersionReglasOperativas.Publicada
            || version.NumeroVersionPublicada != siguiente)
            throw new ArgumentException("La versión publicada no corresponde a la siguiente del operador.", nameof(version));
        VersionVigenteId = version.Id;
        UltimoNumeroPublicacion = siguiente;
        ActualizadoUtc = ValidarUtc(ahoraUtc);
    }

    private static DateTimeOffset ValidarUtc(DateTimeOffset? value)
    {
        var now = value ?? DateTimeOffset.UtcNow;
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(value));
        return now;
    }
}
