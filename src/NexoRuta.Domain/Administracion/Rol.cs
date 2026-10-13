namespace NexoRuta.Domain.Administracion;

public sealed class Rol
{
    public Guid Id { get; private set; }
    public string Codigo { get; private set; } = string.Empty;
    public string Nombre { get; private set; } = string.Empty;
    public TipoAccesoUsuario TipoOrganizacion { get; private set; }
    public Guid? OperadorId { get; private set; }
    public Guid? ComercioId { get; private set; }
    public Guid OrganizacionId { get; private set; }

    private Rol() { } // EF Core

    public Rol(string codigo, string nombre, Guid? operadorId, Guid? comercioId)
    {
        if (operadorId.HasValue == comercioId.HasValue || operadorId == Guid.Empty || comercioId == Guid.Empty)
            throw new ArgumentException("A role must belong to exactly one organization.");
        if (string.IsNullOrWhiteSpace(codigo) || codigo.Trim().Length > 80)
            throw new ArgumentException("A role code of at most 80 characters is required.", nameof(codigo));
        Id = Guid.CreateVersion7();
        Codigo = codigo.Trim().ToLowerInvariant();
        OperadorId = operadorId;
        ComercioId = comercioId;
        OrganizacionId = (operadorId ?? comercioId)!.Value;
        TipoOrganizacion = operadorId.HasValue ? TipoAccesoUsuario.Operador : TipoAccesoUsuario.Comercio;
        Renombrar(nombre);
    }

    public void Renombrar(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre) || nombre.Trim().Length > 160)
            throw new ArgumentException("A role name of at most 160 characters is required.", nameof(nombre));
        Nombre = nombre.Trim();
    }
}
