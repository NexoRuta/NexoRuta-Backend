namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class InstalacionMovil
{
    public Guid Id { get; set; }
    public Guid OperadorId { get; set; }
    public Guid AccesoId { get; set; }
    public Guid SesionId { get; set; }
    public string Plataforma { get; set; } = "";
    public string FidHash { get; set; } = "";
    public byte[] FidCifrado { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public bool Permitida { get; set; }
    public bool Activa { get; set; } = true;
    public DateTimeOffset ActualizadaEnUtc { get; set; }
}
