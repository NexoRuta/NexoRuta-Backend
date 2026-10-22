namespace NexoRuta.Infrastructure.Reparto;
public sealed class PruebaEntrega
{
    public Guid OperadorId { get; init; }
    public Guid EnvioId { get; init; }
    public Guid RutaId { get; init; }
    public string? Nombre { get; set; }
    public string? Documento { get; set; }
    public Guid? FotoId { get; set; }
    public Guid? FirmaId { get; set; }
    public double? Latitud { get; set; }
    public double? Longitud { get; set; }
    public double? PrecisionMetros { get; set; }
    public DateTimeOffset? PosicionEnUtc { get; set; }
}
