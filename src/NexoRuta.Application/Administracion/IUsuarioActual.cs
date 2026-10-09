using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion;

public sealed record ContextoUsuario(
    Guid AccesoId,
    Guid UsuarioId,
    Guid? OperadorId,
    Guid? ComercioId,
    string UsuarioEmail,
    string? OperadorNombre,
    string? ComercioNombre,
    bool EsPropietario)
{
    public string Tipo => ComercioId.HasValue
        ? nameof(TipoAccesoUsuario.Comercio)
        : nameof(TipoAccesoUsuario.Operador);

    public (Guid Id, string Nombre) RequerirComercio()
    {
        var id = ComercioId ?? throw new AccesoNoPermitidoException();
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(ComercioNombre))
            throw new AccesoActualNoDisponibleException();

        return (id, ComercioNombre);
    }
}

public interface IUsuarioActual
{
    Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default);
}

public sealed class AccesoActualNoDisponibleException() : Exception(
    "El usuario actual no tiene un acceso válido a un operador y comercio.");

public sealed class AccesoNoPermitidoException() : Exception("Solo los usuarios de comercio pueden dar de alta envíos.");

public sealed class OperadorNoDisponibleException() : Exception("El operador seleccionado no existe.");
