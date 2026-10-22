namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ParadaRutaDatos(Guid EnvioId, int Orden, bool Activa, string? MotivoSuspension,
    DateTimeOffset LlegadaEstimadaUtc, string Estado, long EnvioVersion, string Destinatario,
    string Direccion, double Latitud, double Longitud, IReadOnlyList<BultoRutaDatos> Bultos, ReglasEnvioDatos Reglas);
