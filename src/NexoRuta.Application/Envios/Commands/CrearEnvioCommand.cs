namespace NexoRuta.Application.Envios.Commands;

public sealed record BultoDeclarado(string Codigo, decimal PesoGramos, decimal LargoCentimetros,
    decimal AnchoCentimetros, decimal AltoCentimetros);

public sealed record CrearEnvioCommand(Guid OperadorId, string DestinatarioNombre,
    string DestinatarioEmail, string Direccion, IReadOnlyList<BultoDeclarado> Bultos, ServicioEnvioDeclarado Servicio)
{
    public SolicitudRetiro? Retiro { get; init; }
}
public sealed record SolicitudRetiro(Guid SolicitudId, string Motivo, DateTimeOffset DesdeUtc, DateTimeOffset HastaUtc);

public sealed record ServicioEnvioDeclarado(Guid ZonaId, double Latitud, double Longitud, string Modalidad)
{
    public Guid? VersionTarifarioEsperada { get; init; }
}
