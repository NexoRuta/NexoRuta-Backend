namespace NexoRuta.Domain.Administracion;

public sealed class Operador
{
    public Guid Id { get; private set; }
    public string Nombre { get; private set; } = "";
    public long Version { get; private set; }
    public EstadoOrganizacion Estado { get; private set; } = EstadoOrganizacion.Activa;

    private Operador() { } // EF Core

    public Operador(string nombre, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre es obligatorio.", nameof(nombre));

        if (id == Guid.Empty) throw new ArgumentException("The organization ID cannot be empty.", nameof(id));
        Id = id ?? Guid.CreateVersion7();
        Nombre = nombre.Trim();
    }

    public void ActualizarNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 160)
            throw new ArgumentException("El nombre es obligatorio y no puede superar los 160 caracteres.", nameof(nombre));

        Nombre = nombre.Trim();
        Version = checked(Version + 1);
    }

    public bool CambiarEstado(EstadoOrganizacion estado)
    {
        if (estado is not (EstadoOrganizacion.Activa or EstadoOrganizacion.Suspendida))
            throw new ArgumentOutOfRangeException(nameof(estado));
        if (Estado == estado) return false;
        Estado = estado;
        Version = checked(Version + 1);
        return true;
    }
}
