namespace NexoRuta.Application.Reparto.Dtos;
public sealed record ResultadoOperacionReparto(Guid Id, long Secuencia, string Resultado,
    string? Detalle, long? VersionEnvio, long VersionRuta, IReadOnlyDictionary<Guid, long> VersionesEnvio, IReadOnlyDictionary<Guid, string> EstadosEnvio);
