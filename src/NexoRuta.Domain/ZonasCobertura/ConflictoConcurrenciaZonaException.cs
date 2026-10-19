namespace NexoRuta.Domain.ZonasCobertura;

public sealed class ConflictoConcurrenciaZonaException(long versionEsperada, long? versionActual)
    : InvalidOperationException("La configuración de la zona cambió; recargá antes de volver a guardar.")
{
    public long VersionEsperada { get; } = versionEsperada;
    public long? VersionActual { get; } = versionActual;
}
