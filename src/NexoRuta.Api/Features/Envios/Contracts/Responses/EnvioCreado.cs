namespace NexoRuta.Api.Features.Envios.Contracts.Responses;

// Keep the published schema names and flat JSON independent of Application results.
public sealed record EnvioCreado(
    Guid Id,
    Guid OperadorId,
    Guid ComercioId,
    Guid CreadoPorUsuarioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre,
    IReadOnlyList<BultoDetalle> Bultos,
    DateTimeOffset CreadoEnUtc, TarifaEnvioResponse Tarifa)
{

    public static EnvioCreado Desde(Application.Envios.Results.EnvioCreado envio) => new(
        Id: envio.Id,
        OperadorId: envio.Origen.OperadorId,
        ComercioId: envio.Origen.ComercioId,
        CreadoPorUsuarioId: envio.Origen.CreadoPorUsuarioId,
        UsuarioEmail: envio.Origen.UsuarioEmail,
        OperadorNombre: envio.Origen.OperadorNombre,
        ComercioNombre: envio.Origen.ComercioNombre,
        Bultos: envio.Bultos.Select(b => new BultoDetalle(b.Codigo, b.PesoGramos,
            b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros) { Custodia = b.Custodia }).ToArray(),
        CreadoEnUtc: envio.CreadoEnUtc, Tarifa: TarifaEnvioResponse.Desde(envio.Tarifa));
}
