namespace NexoRuta.Application.Reparto.Dtos;

public sealed record ConfiguracionRepartoDatos(long Version, string ZonaHoraria, int MaximoParadas, int MaximoIntentos,
    bool RequiereFirma, bool RequiereFoto, bool RequiereNombre, bool RequiereDocumento,
    bool RequiereFotoIntento, bool BloquearDiscrepanciasCarga, string[] Motivos);
