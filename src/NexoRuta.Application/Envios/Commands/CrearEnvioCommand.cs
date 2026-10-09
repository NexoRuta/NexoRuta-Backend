namespace NexoRuta.Application.Envios;

public sealed record CrearEnvioCommand(
    Guid OperadorId,
    string DestinatarioNombre,
    string Direccion,
    string CodigoBulto,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);
