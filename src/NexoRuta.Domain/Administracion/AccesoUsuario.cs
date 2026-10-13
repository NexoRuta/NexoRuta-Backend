
namespace NexoRuta.Domain.Administracion;

public sealed class AccesoUsuario
{
    public Guid Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? ComercioId { get; private set; }
    public Guid? RolId { get; private set; }

    public TipoAccesoUsuario TipoOrganizacion { get; private set; }
    public Guid OrganizacionId { get; private set; }

    private AccesoUsuario() { } // EF Core

    public AccesoUsuario(
        Guid usuarioId,
        Guid? operadorId,
        Guid? comercioId,
        Rol? rol = null)
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

        OrganizacionId = (operadorId ?? comercioId)!.Value;
        TipoOrganizacion = operadorId.HasValue ? TipoAccesoUsuario.Operador : TipoAccesoUsuario.Comercio;
        if (rol is not null) AsignarRol(rol);
    }

    public void RetirarRol() => RolId = null;

    public void AsignarRol(Rol rol)
    {
        ArgumentNullException.ThrowIfNull(rol);

        var tipoOrganizacion = OperadorId.HasValue
            ? TipoAccesoUsuario.Operador
            : TipoAccesoUsuario.Comercio;

        if (rol.TipoOrganizacion != tipoOrganizacion || rol.OperadorId != OperadorId || rol.ComercioId != ComercioId)
            throw new InvalidOperationException(
                "El rol no corresponde al tipo de organización.");

        RolId = rol.Id;
    }
}
