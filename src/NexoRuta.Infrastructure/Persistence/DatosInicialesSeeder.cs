
using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DatosInicialesSeeder(
    NexoRutaDbContext db,
    AccesoInicial accesoInicial)
{
    public async Task SeedAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(
            accesoInicial.UsuarioEmail,
            accesoInicial.OperadorUsuarioEmail,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Las cuentas iniciales deben ser distintas.");
        }

        await using var transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        // Catálogo de permisos
        var usuariosGestionar = await ObtenerPermisoAsync(
            "usuarios.gestionar", "Gestionar usuarios", cancellationToken);

        var configuracionGestionar = await ObtenerPermisoAsync(
            "configuracion.gestionar", "Gestionar configuración",
            cancellationToken);

        var enviosCrear = await ObtenerPermisoAsync(
            "envios.crear", "Crear envíos", cancellationToken);

        var enviosConsultar = await ObtenerPermisoAsync(
            "envios.consultar", "Consultar envíos", cancellationToken);

        var enviosGestionar = await ObtenerPermisoAsync(
            "envios.gestionar", "Gestionar envíos", cancellationToken);

        // Catálogo de roles
        var administradorOperador = await ObtenerRolAsync(
            "administrador_operador",
            "Administrador de operador",
            TipoAccesoUsuario.Operador,
            cancellationToken);

        var usuarioOperador = await ObtenerRolAsync(
            "usuario_operador",
            "Usuario de operador",
            TipoAccesoUsuario.Operador,
            cancellationToken);

        var propietarioComercio = await ObtenerRolAsync(
            "propietario_comercio",
            "Propietario de comercio",
            TipoAccesoUsuario.Comercio,
            cancellationToken);

        var usuarioComercio = await ObtenerRolAsync(
            "usuario_comercio",
            "Usuario de comercio",
            TipoAccesoUsuario.Comercio,
            cancellationToken);

        // Permisos de cada rol
        await AsociarPermisosAsync(
            administradorOperador,
            cancellationToken,
            usuariosGestionar,
            configuracionGestionar,
            enviosConsultar,
            enviosGestionar);

        await AsociarPermisosAsync(
            usuarioOperador,
            cancellationToken,
            enviosConsultar,
            enviosGestionar);

        await AsociarPermisosAsync(
            propietarioComercio,
            cancellationToken,
            usuariosGestionar,
            enviosCrear,
            enviosConsultar);

        await AsociarPermisosAsync(
            usuarioComercio,
            cancellationToken,
            enviosCrear,
            enviosConsultar);

        // Organizaciones
        var operador = await db.Operadores.SingleOrDefaultAsync(
            x => x.Nombre == accesoInicial.OperadorNombre,
            cancellationToken);

        if (operador is null)
        {
            operador = new Operador(accesoInicial.OperadorNombre);
            db.Operadores.Add(operador);
        }

        var comercio = await db.Comercios.SingleOrDefaultAsync(
            x => x.Nombre == accesoInicial.ComercioNombre,
            cancellationToken);

        if (comercio is null)
        {
            comercio = new Comercio(accesoInicial.ComercioNombre);
            db.Comercios.Add(comercio);
        }

        // Usuarios
        var cuentaOperador = await ObtenerUsuarioAsync(
            accesoInicial.OperadorUsuarioEmail,
            cancellationToken);

        var cuentaComercio = await ObtenerUsuarioAsync(
            accesoInicial.UsuarioEmail,
            cancellationToken);

        // Accesos con rol obligatorio
        await AsegurarAccesoAsync(
            cuentaOperador,
            operador.Id,
            null,
            administradorOperador,
            cancellationToken);

        await AsegurarAccesoAsync(
            cuentaComercio,
            null,
            comercio.Id,
            propietarioComercio,
            cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Rol> ObtenerRolAsync(
        string codigo,
        string nombre,
        TipoAccesoUsuario tipo,
        CancellationToken ct)
    {
        var rol = await db.Roles.SingleOrDefaultAsync(
            x => x.Codigo == codigo, ct);

        if (rol is not null)
        {
            if (rol.TipoOrganizacion != tipo)
                throw new InvalidOperationException(
                    $"El rol {codigo} tiene un tipo incompatible.");

            return rol;
        }

        rol = new Rol(codigo, nombre, tipo);
        db.Roles.Add(rol);
        return rol;
    }

    private async Task<Permiso> ObtenerPermisoAsync(
        string codigo,
        string nombre,
        CancellationToken ct)
    {
        var permiso = await db.Permisos.SingleOrDefaultAsync(
            x => x.Codigo == codigo, ct);

        if (permiso is not null)
            return permiso;

        permiso = new Permiso(codigo, nombre);
        db.Permisos.Add(permiso);
        return permiso;
    }

    private async Task AsociarPermisosAsync(
        Rol rol,
        CancellationToken ct,
        params Permiso[] permisos)
    {
        foreach (var permiso in permisos)
        {
            var existe = await db.RolesPermisos.AnyAsync(
                x => x.RolId == rol.Id &&
                     x.PermisoId == permiso.Id,
                ct);

            if (!existe)
            {
                db.RolesPermisos.Add(
                    new RolPermiso(rol.Id, permiso.Id));
            }
        }
    }

    private async Task<Usuario> ObtenerUsuarioAsync(
        string email,
        CancellationToken ct)
    {
        var usuario = await db.Usuarios.SingleOrDefaultAsync(
            x => x.Email == email, ct);

        if (usuario is not null)
            return usuario;

        usuario = new Usuario(email);
        db.Usuarios.Add(usuario);
        return usuario;
    }

    private async Task AsegurarAccesoAsync(
        Usuario usuario,
        Guid? operadorId,
        Guid? comercioId,
        Rol rol,
        CancellationToken ct)
    {
        var acceso = await db.AccesosUsuario.SingleOrDefaultAsync(
            x => x.UsuarioId == usuario.Id,
            ct);

        if (acceso is not null)
        {
            if (acceso.OperadorId != operadorId ||
                acceso.ComercioId != comercioId ||
                acceso.RolId != rol.Id)
            {
                throw new InvalidOperationException(
                    "La cuenta inicial tiene una organización " +
                    "o un rol incompatible.");
            }

            return;
        }

        db.AccesosUsuario.Add(
            new AccesoUsuario(
                usuario.Id,
                operadorId,
                comercioId,
                rol));
    }
}
