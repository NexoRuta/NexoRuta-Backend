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
}

public interface IUsuarioActual
{
    Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default);
}
