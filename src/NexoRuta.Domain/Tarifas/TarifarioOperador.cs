namespace NexoRuta.Domain.Tarifas;

public sealed class TarifarioOperador
{
    public Guid OperadorId { get; private set; }
    public Guid? VersionVigenteId { get; private set; }
    public int UltimoNumeroPublicacion { get; private set; }
    public DateTimeOffset ActualizadoUtc { get; private set; }

    private TarifarioOperador() { }

    public static TarifarioOperador Crear(Guid operadorId, DateTimeOffset? ahoraUtc = null)
    {
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        var now = ValidarUtc(ahoraUtc);
        return new() { OperadorId = operadorId, ActualizadoUtc = now };
    }

    public void Publicar(VersionTarifario version, DateTimeOffset? ahoraUtc = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.OperadorId != OperadorId || version.Estado != EstadoVersionTarifario.Publicada
            || version.NumeroVersionPublicada is null)
            throw new ArgumentException("La versión publicada no pertenece a este tarifario.", nameof(version));
        var expected = checked(UltimoNumeroPublicacion + 1);
        if (version.NumeroVersionPublicada != expected)
            throw new InvalidOperationException("El número de publicación no es el siguiente del tarifario.");
        VersionVigenteId = version.Id;
        UltimoNumeroPublicacion = expected;
        ActualizadoUtc = ValidarUtc(ahoraUtc);
    }

    private static DateTimeOffset ValidarUtc(DateTimeOffset? value)
    {
        var now = value ?? DateTimeOffset.UtcNow;
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("La fecha debe estar en UTC.", nameof(value));
        return now;
    }
}
