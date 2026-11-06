namespace NexoRuta.Application.Reparto.Dtos;

public sealed record SolicitarReprogramacionEnvioDatos(long VersionEnvio)
{
    public Guid? SolicitudPublicaId { get; init; }
}
