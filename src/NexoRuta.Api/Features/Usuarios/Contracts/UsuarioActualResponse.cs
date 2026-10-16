using NexoRuta.Application.Administracion.Context;

namespace NexoRuta.Api.Features.Usuarios.Contracts;

public sealed record UsuarioActualResponse(Guid AccesoId, Guid UsuarioId, Guid? OperadorId,
    Guid? ComercioId, string UsuarioEmail, string? OperadorNombre, string? ComercioNombre,
    Guid? RolId, string? RolCodigo, string Tipo, int? TipoAcceso)
{
    public static UsuarioActualResponse Desde(ContextoUsuario actor) => new(actor.AccesoId, actor.UsuarioId,
        actor.OperadorId, actor.ComercioId, actor.UsuarioEmail, actor.OperadorNombre, actor.ComercioNombre,
        actor.RolId, actor.RolCodigo, actor.Tipo, (int)actor.TipoAcceso);

    public static UsuarioActualResponse Desde(ContextoPlataforma actor) => new(actor.AccesoId, actor.UsuarioId,
        null, null, actor.UsuarioEmail, null, null, null, null, "Plataforma", null);
}
