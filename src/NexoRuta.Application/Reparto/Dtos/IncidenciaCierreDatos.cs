namespace NexoRuta.Application.Reparto.Dtos;

public sealed record IncidenciaCierreDatos(Guid OperacionId, Guid EnvioId, Guid BultoId, string Codigo,
    long VersionBulto, DateTimeOffset OcurridaEnUtc, DateTimeOffset RecibidaEnUtc);
