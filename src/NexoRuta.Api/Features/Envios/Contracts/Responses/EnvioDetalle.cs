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
    IReadOnlyList<BultoDetalle> Bultos)
{
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
        Bultos: envio.Bultos.Select(bulto => new BultoDetalle(
            Codigo: bulto.Codigo,
            PesoGramos: bulto.PesoGramos,
            LargoCentimetros: bulto.LargoCentimetros,
            AnchoCentimetros: bulto.AnchoCentimetros,
            AltoCentimetros: bulto.AltoCentimetros)).ToArray());
}
