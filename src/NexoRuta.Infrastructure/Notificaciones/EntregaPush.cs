namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class EntregaPush
{
    public Guid OperadorId { get; set; }
    public Guid AvisoId { get; set; }
    public Guid InstalacionId { get; set; }
    public string FidHash { get; set; } = "";
    public string Resultado { get; set; } = "";
    public string? IdFcm { get; set; }
    public DateTimeOffset RegistradaEnUtc { get; set; }
}
