namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

public sealed class TokenRenovacionMovil
{
    public string Hash { get; set; } = null!;
    public Guid SesionId { get; set; }
    public DateTimeOffset? ConsumidoEnUtc { get; set; }
}
