namespace NexoRuta.Domain.Administracion;

public sealed record DefinicionPermiso(string Codigo, string Nombre, IReadOnlyList<TipoAccesoUsuario> Ambitos)
{
    public bool AplicaA(TipoAccesoUsuario ambito) => Ambitos.Contains(ambito);
}

public static class CatalogoPermisos
{
    // Platform grants have no organizational scope and never enter Para/EsAplicable.
    public static IReadOnlyList<DefinicionPermiso> Plataforma { get; } = Array.AsReadOnly<DefinicionPermiso>(
        [new(RolPlataforma.GestionarOrganizaciones, "Manage platform organizations", [])]);

    private static readonly IReadOnlyList<DefinicionPermiso> Definiciones = Array.AsReadOnly<DefinicionPermiso>(
    [
        new("usuarios.gestionar", "usuarios.gestionar", [TipoAccesoUsuario.Operador, TipoAccesoUsuario.Comercio]),
        new("configuracion.gestionar", "configuracion.gestionar", [TipoAccesoUsuario.Operador, TipoAccesoUsuario.Comercio]),
        new("envios.crear", "envios.crear", [TipoAccesoUsuario.Comercio]),
        new("envios.consultar", "envios.consultar", [TipoAccesoUsuario.Operador, TipoAccesoUsuario.Comercio]),
        new("envios.gestionar", "envios.gestionar", [TipoAccesoUsuario.Operador])
    ]);

    public static IReadOnlyList<DefinicionPermiso> Todos => Definiciones;

    public static IReadOnlyList<DefinicionPermiso> Para(TipoAccesoUsuario ambito)
        => Definiciones.Where(definicion => definicion.AplicaA(ambito)).ToArray();

    public static bool EsAplicable(string codigo, TipoAccesoUsuario ambito)
        => Definiciones.Any(definicion => definicion.Codigo == codigo && definicion.AplicaA(ambito));
}
