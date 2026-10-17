using System.Text.Json;
namespace NexoRuta.Application.Reparto.Dtos;
public sealed record OperacionRepartoDatos(Guid Id, long Secuencia, string Tipo, Guid? EnvioId,
    long? VersionEnvio, DateTimeOffset OcurridaEn, JsonElement Datos);
