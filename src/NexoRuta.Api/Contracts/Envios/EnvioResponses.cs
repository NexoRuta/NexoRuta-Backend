namespace NexoRuta.Api.Contracts.Envios;

// Keep the published schema names and flat JSON independent of Application results.
public sealed record EnvioCreado(
    Guid Id,
    Guid OperadorId,
    Guid ComercioId,
    Guid CreadoPorUsuarioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre,
    string CodigoBulto,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros)
{
    public static EnvioCreado Desde(Application.Envios.Results.EnvioCreado envio) => new(
        Id: envio.Id,
        OperadorId: envio.Origen.OperadorId,
        ComercioId: envio.Origen.ComercioId,
        CreadoPorUsuarioId: envio.Origen.CreadoPorUsuarioId,
        UsuarioEmail: envio.Origen.UsuarioEmail,
        OperadorNombre: envio.Origen.OperadorNombre,
        ComercioNombre: envio.Origen.ComercioNombre,
        CodigoBulto: envio.Bulto.Codigo,
        PesoGramos: envio.Bulto.PesoGramos,
        LargoCentimetros: envio.Bulto.LargoCentimetros,
        AnchoCentimetros: envio.Bulto.AnchoCentimetros,
        AltoCentimetros: envio.Bulto.AltoCentimetros);
}

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

public sealed record BultoDetalle(
    string Codigo,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);
