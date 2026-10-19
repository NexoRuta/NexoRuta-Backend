namespace NexoRuta.Application.Envios.Commands;

public sealed record CrearEnvioCommand(
    Guid OperadorId,
    string DestinatarioNombre,
    string DestinatarioEmail,
    string Direccion,
    string CodigoBulto,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);
