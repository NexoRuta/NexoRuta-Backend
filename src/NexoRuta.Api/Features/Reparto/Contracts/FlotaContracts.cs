using System.ComponentModel.DataAnnotations;
using NexoRuta.Application.Reparto.Dtos;

namespace NexoRuta.Api.Features.Reparto.Contracts;

public sealed record ComandoFlotaRequest([Required] Guid ComandoId, Guid? OcupacionId,
    [Range(0, long.MaxValue)] long Version, [Required, StringLength(500)] string Motivo,
    Guid? EnvioId = null, [Range(0, long.MaxValue)] long? VersionEnvio = null)
{
    public ComandoFlotaDatos Datos() => new(ComandoId, OcupacionId, Version, Motivo, EnvioId, VersionEnvio);
}
public sealed record ResultadoFlotaResponse(Guid ComandoId, Guid VehiculoId, Guid? RutaId, Guid? OcupacionId,
    long Version, string Accion, DateTimeOffset ConfirmadoEnUtc, Guid? EnvioId, long? VersionEnvio, IReadOnlyDictionary<Guid, long>? VersionesBultosAnteriores)
{
    public static ResultadoFlotaResponse Desde(ResultadoFlotaDatos d) => new(d.ComandoId, d.VehiculoId, d.RutaId, d.OcupacionId, d.Version, d.Accion, d.ConfirmadoEnUtc, d.EnvioId, d.VersionEnvio, d.VersionesBultosAnteriores);
}
public sealed record OcupacionFlotaResponse(Guid Id, Guid RutaId, Guid RepartidorAccesoId, DateOnly Fecha,
    string EstadoRuta, long Version, bool Legada, DateTimeOffset? AdquiridaEnUtc, DateTimeOffset? RetornoEnUtc,
    int CargaPendiente, bool PuedeAnular);
public sealed record VehiculoFlotaResponse(Guid Id, string Matricula, bool Activo, long Version,
    DateTimeOffset? VerificacionInicialEnUtc, int CargaPendiente, IReadOnlyList<OcupacionFlotaResponse> Ocupaciones,
    DateTimeOffset? UltimaLiberacionEnUtc, IReadOnlyList<CargaInicialFlotaResponse> CargasIniciales)
{
    public static VehiculoFlotaResponse Desde(VehiculoFlotaDatos d) => new(d.Id, d.Matricula, d.Activo, d.Version, d.VerificacionInicialEnUtc,
        d.CargaPendiente, d.Ocupaciones.Select(o => new OcupacionFlotaResponse(o.Id, o.RutaId, o.RepartidorAccesoId, o.Fecha, o.EstadoRuta,
            o.Version, o.Legada, o.AdquiridaEnUtc, o.RetornoEnUtc, o.CargaPendiente, o.PuedeAnular)).ToArray(), d.UltimaLiberacionEnUtc,
        d.CargasIniciales.Select(c => new CargaInicialFlotaResponse(c.EnvioId, c.VersionEnvio, c.RutaId, c.Fecha, c.BultosPendientes)).ToArray());
}
public sealed record CargaInicialFlotaResponse(Guid EnvioId, long VersionEnvio, Guid RutaId, DateOnly Fecha, int BultosPendientes);
