using NexoRuta.Domain.Administracion;

namespace NexoRuta.Domain.Reparto;

public sealed class Repartidor
{
    public Guid AccesoId { get; private set; }
    public Guid OperadorId { get; private set; }
    public TipoAccesoUsuario TipoOrganizacion { get; private set; } = TipoAccesoUsuario.Operador;
    public string Nombre { get; private set; } = "";
    public bool Activo { get; private set; } = true;
    public long Version { get; private set; }

    private Repartidor() { }

    public Repartidor(Guid accesoId, Guid operadorId, string nombre)
    {
        if (accesoId == Guid.Empty || operadorId == Guid.Empty)
            throw new ArgumentException("El repartidor requiere un acceso y un operador.");
        AccesoId = accesoId;
        OperadorId = operadorId;
        Nombre = NombreValido(nombre);
    }

    public void Actualizar(string nombre, bool activo)
    {
        Nombre = NombreValido(nombre);
        Activo = activo;
        Version++;
    }

    private static string NombreValido(string nombre) => !string.IsNullOrWhiteSpace(nombre) && nombre.Trim().Length <= 120
        ? nombre.Trim() : throw new ArgumentException("El nombre del repartidor es obligatorio y admite hasta 120 caracteres.");
}
