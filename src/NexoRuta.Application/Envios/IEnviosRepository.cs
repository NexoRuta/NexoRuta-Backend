using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios;

public sealed record DemoComercioContext(
    Guid UsuarioId,
    Guid OperadorId,
    Guid OperadorComercioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre);

public sealed record CrearEnvioCommand(
    string DestinatarioNombre,
    string Direccion,
    string CodigoBulto,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);

public sealed record EnvioCreado(
    Guid Id,
    Guid OperadorId,
    Guid OperadorComercioId,
    Guid CreadoPorUsuarioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre,
    string CodigoBulto,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);

public sealed record BultoDetalle(
    string Codigo,
    decimal PesoGramos,
    decimal LargoCentimetros,
    decimal AnchoCentimetros,
    decimal AltoCentimetros);

public sealed record EnvioDetalle(
    Guid Id,
    Guid OperadorId,
    Guid OperadorComercioId,
    Guid CreadoPorUsuarioId,
    string UsuarioEmail,
    string OperadorNombre,
    string ComercioNombre,
    string DestinatarioNombre,
    string Direccion,
    string Estado,
    IReadOnlyList<BultoDetalle> Bultos);

public interface IEnviosRepository
{
    Task<DemoComercioContext?> ObtenerContextoDemoAsync(CancellationToken cancellationToken = default);

    Task GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        Bulto bulto,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
        DemoComercioContext contexto,
        CancellationToken cancellationToken = default);
}
