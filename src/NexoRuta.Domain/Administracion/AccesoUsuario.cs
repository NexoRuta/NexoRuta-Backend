namespace NexoRuta.Domain.Administracion;

public sealed class AccesoUsuario
{
    public Guid Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? ComercioId { get; private set; }
    public bool EsPropietario { get; private set; }

    public AccesoUsuario(Guid usuarioId, Guid? operadorId, Guid? comercioId, bool esPropietario = false)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (operadorId.HasValue == comercioId.HasValue || operadorId == Guid.Empty || comercioId == Guid.Empty)
            throw new ArgumentException("La cuenta debe pertenecer a un operador o a un comercio.");
        if (esPropietario && comercioId is null)
            throw new ArgumentException("Solo una cuenta de comercio puede ser su propietaria.", nameof(esPropietario));

        Id = Guid.CreateVersion7();
        UsuarioId = usuarioId;
        OperadorId = operadorId;
        ComercioId = comercioId;
        EsPropietario = esPropietario;
    }
}
