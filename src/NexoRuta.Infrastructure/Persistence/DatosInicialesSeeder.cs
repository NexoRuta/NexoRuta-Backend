using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Administracion;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DatosInicialesSeeder(NexoRutaDbContext db, AccesoInicial accesoInicial)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (string.Equals(accesoInicial.UsuarioEmail, accesoInicial.OperadorUsuarioEmail, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("El dueño del comercio y el usuario del operador deben ser cuentas distintas.");

        var usuarioComercio = await db.Usuarios.SingleOrDefaultAsync(
            x => x.Email == accesoInicial.UsuarioEmail, cancellationToken);
        var usuarioOperador = await db.Usuarios.SingleOrDefaultAsync(
            x => x.Email == accesoInicial.OperadorUsuarioEmail, cancellationToken);
        var cuentaComercio = usuarioComercio is null ? null :
            await db.AccesosUsuario.SingleOrDefaultAsync(x => x.UsuarioId == usuarioComercio.Id, cancellationToken);
        var cuentaOperador = usuarioOperador is null ? null :
            await db.AccesosUsuario.SingleOrDefaultAsync(x => x.UsuarioId == usuarioOperador.Id, cancellationToken);
        if (cuentaComercio?.OperadorId is not null || cuentaOperador?.ComercioId is not null)
            throw new InvalidOperationException("Las cuentas iniciales no pertenecen al tipo de organización previsto.");

        var comercio = cuentaComercio?.ComercioId is { } comercioId
            ? await db.Comercios.SingleAsync(x => x.Id == comercioId, cancellationToken)
            : await db.Comercios.SingleOrDefaultAsync(x => x.Nombre == accesoInicial.ComercioNombre, cancellationToken);
        var operador = cuentaOperador?.OperadorId is { } operadorId
            ? await db.Operadores.SingleAsync(x => x.Id == operadorId, cancellationToken)
            : await db.Operadores.SingleOrDefaultAsync(x => x.Nombre == accesoInicial.OperadorNombre, cancellationToken);

        if (operador is null)
            db.Operadores.Add(operador = new Operador(accesoInicial.OperadorNombre));
        if (comercio is null)
            db.Comercios.Add(comercio = new Comercio(accesoInicial.ComercioNombre));
        if (usuarioComercio is null)
            db.Usuarios.Add(usuarioComercio = new Usuario(accesoInicial.UsuarioEmail));
        if (usuarioOperador is null)
            db.Usuarios.Add(usuarioOperador = new Usuario(accesoInicial.OperadorUsuarioEmail));

        if (cuentaComercio is null)
            db.AccesosUsuario.Add(new AccesoUsuario(usuarioComercio.Id, null, comercio.Id, esPropietario: true));
        else
            db.Entry(cuentaComercio).Property(x => x.EsPropietario).CurrentValue = true;
        if (cuentaOperador is null)
            db.AccesosUsuario.Add(new AccesoUsuario(usuarioOperador.Id, operador.Id, null));
        await db.SaveChangesAsync(cancellationToken);
    }
}
