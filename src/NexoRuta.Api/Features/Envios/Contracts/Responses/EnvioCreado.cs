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
