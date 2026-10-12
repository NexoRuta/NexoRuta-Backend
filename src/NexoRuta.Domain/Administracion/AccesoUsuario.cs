
namespace NexoRuta.Domain.Administracion;

public sealed class AccesoUsuario
{
    public Guid Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? ComercioId { get; private set; }
    public Guid RolId { get; private set; }

    private AccesoUsuario() { } // EF Core

    public AccesoUsuario(
        Guid usuarioId,
        Guid? operadorId,
        Guid? comercioId,
        Rol rol)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException(
                "El usuario es obligatorio.",
                nameof(usuarioId));

        if (operadorId.HasValue == comercioId.HasValue ||
            operadorId == Guid.Empty ||
            comercioId == Guid.Empty)
            throw new ArgumentException(
                "La cuenta debe pertenecer a un operador o a un comercio.");

        Id = Guid.CreateVersion7();
        UsuarioId = usuarioId;
        OperadorId = operadorId;
        ComercioId = comercioId;

        AsignarRol(rol);
    }

    public void AsignarRol(Rol rol)
    {
        ArgumentNullException.ThrowIfNull(rol);

        var tipoOrganizacion = OperadorId.HasValue
            ? TipoAccesoUsuario.Operador
            : TipoAccesoUsuario.Comercio;

        if (rol.TipoOrganizacion != tipoOrganizacion)
            throw new InvalidOperationException(
                "El rol no corresponde al tipo de organización.");

        RolId = rol.Id;
    }
}
