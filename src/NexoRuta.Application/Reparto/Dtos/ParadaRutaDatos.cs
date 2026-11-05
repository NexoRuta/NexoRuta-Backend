namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ParadaRutaDatos(Guid EnvioId, int Orden, bool Activa, string? MotivoSuspension,
    DateTimeOffset LlegadaEstimadaUtc, string Estado, long EnvioVersion, string Destinatario,
    string Direccion, double Latitud, double Longitud, IReadOnlyList<BultoRutaDatos> Bultos, ReglasEnvioDatos Reglas,
    int IntentosFallidos, DateTimeOffset? ProximoIntentoElegibleUtc, int? ServicioMinutos = null) { public bool EsRetiro { get; init; } public string ResultadoVisita { get; init; } = "Pendiente"; }
