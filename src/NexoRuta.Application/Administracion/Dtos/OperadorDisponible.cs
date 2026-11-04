namespace NexoRuta.Application.Administracion.Dtos;

public sealed record OperadorDisponible(Guid OperadorId, string Nombre);

public sealed record OperadorDisponibleParaImportacion(Guid OperadorId, string Nombre, bool Activo);
