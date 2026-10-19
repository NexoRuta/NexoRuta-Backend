namespace NexoRuta.Domain.ZonasCobertura;

public sealed record FranjaEntregaInicial(
    DiaSemanaIso DiaSemanaIso,
    TimeOnly HoraInicio,
    TimeOnly HoraFin,
    EstadoFranjaEntrega Estado = EstadoFranjaEntrega.Activa);
