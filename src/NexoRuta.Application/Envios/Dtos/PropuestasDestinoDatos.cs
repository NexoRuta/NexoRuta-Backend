namespace NexoRuta.Application.Envios.Dtos;

public sealed record PropuestasDestinoDatos(Guid EnvioId, string Direccion, long Version,
    string Atribucion, IReadOnlyList<PropuestaDestinoDatos> Propuestas);
