namespace NexoRuta.Api.Features.Envios.Contracts.Responses;

public sealed record EnvioDetalle(
    Guid Id,
    Guid OperadorId,
    Guid ComercioId,
    Guid CreadoPorUsuarioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre,
    string DestinatarioNombre,
    string Direccion,
    string Estado,
    long Version,
    IReadOnlyList<BultoDetalle> Bultos,
    DateTimeOffset CreadoEnUtc)
{
    public bool DestinoConfirmado { get; init; }
    public string? ReferenciaExterna { get; init; }
    public string DestinatarioEmail { get; init; } = string.Empty;

    public static EnvioDetalle Desde(Application.Envios.Results.EnvioDetalle envio) => new(
        Id: envio.Id,
        OperadorId: envio.Origen.OperadorId,
        ComercioId: envio.Origen.ComercioId,
        CreadoPorUsuarioId: envio.Origen.CreadoPorUsuarioId,
        UsuarioEmail: envio.Origen.UsuarioEmail,
        OperadorNombre: envio.Origen.OperadorNombre,
        ComercioNombre: envio.Origen.ComercioNombre,
        DestinatarioNombre: envio.DestinatarioNombre,
        Direccion: envio.Direccion,
        Estado: envio.Estado,
        Version: envio.Version,
        CreadoEnUtc: envio.CreadoEnUtc,
        Bultos: envio.Bultos.Select(bulto => new BultoDetalle(
            Codigo: bulto.Codigo,
            PesoGramos: bulto.PesoGramos,
            LargoCentimetros: bulto.LargoCentimetros,
            AnchoCentimetros: bulto.AnchoCentimetros,
            AltoCentimetros: bulto.AltoCentimetros) { Custodia = bulto.Custodia }).ToArray())
    {
        DestinoConfirmado = envio.DestinoConfirmado,
        ReferenciaExterna = envio.ReferenciaExterna,
        DestinatarioEmail = envio.DestinatarioEmail
    };
}
