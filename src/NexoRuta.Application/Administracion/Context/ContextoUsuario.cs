using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Administracion.Context;

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
