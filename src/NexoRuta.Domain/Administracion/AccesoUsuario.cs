namespace NexoRuta.Domain.Administracion;

public sealed class AccesoUsuario
{
    public Guid Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public Guid OperadorId { get; private set; }
    public Guid OperadorComercioId { get; private set; }

    public AccesoUsuario(Guid usuarioId, Guid operadorId, Guid operadorComercioId)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El usuario es obligatorio.", nameof(usuarioId));
        if (operadorId == Guid.Empty)
            throw new ArgumentException("El operador es obligatorio.", nameof(operadorId));
        if (operadorComercioId == Guid.Empty)
            throw new ArgumentException("El vínculo entre operador y comercio es obligatorio.", nameof(operadorComercioId));

        Id = Guid.CreateVersion7();
        UsuarioId = usuarioId;
        OperadorId = operadorId;
        OperadorComercioId = operadorComercioId;
    }
}
