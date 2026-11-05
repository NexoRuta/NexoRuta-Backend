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
    public TarifaEnvioResponse? Tarifa { get; init; }
    public bool EsRetiro { get; init; }
    public string? MotivoRetiro { get; init; }
    public DateTimeOffset? FranjaRetiroDesdeUtc { get; init; }
    public DateTimeOffset? FranjaRetiroHastaUtc { get; init; }
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
            AltoCentimetros: bulto.AltoCentimetros) { Custodia = bulto.Custodia, Resultado = bulto.Resultado, MotivoNoEntrega = bulto.MotivoNoEntrega }).ToArray())
    {
        Tarifa = envio.Tarifa is null ? null : TarifaEnvioResponse.Desde(envio.Tarifa),
        EsRetiro = envio.EsRetiro, MotivoRetiro = envio.MotivoRetiro, FranjaRetiroDesdeUtc = envio.FranjaRetiroDesdeUtc, FranjaRetiroHastaUtc = envio.FranjaRetiroHastaUtc,
        DestinoConfirmado = envio.DestinoConfirmado,
        ReferenciaExterna = envio.ReferenciaExterna,
        DestinatarioEmail = envio.DestinatarioEmail
    };
}
