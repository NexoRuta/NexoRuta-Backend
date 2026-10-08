using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DemoDataSeeder(NexoRutaDbContext db)
{
    public const string UsuarioEmail = "demo@nexoruta.local";

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.Usuarios.AnyAsync(x => x.Email == UsuarioEmail, cancellationToken))
            return;

        var operador = new Operador("Operador Demo");
        var comercio = new Comercio("Comercio Demo");
        var usuario = new Usuario(UsuarioEmail);
        var relacion = new OperadorComercio(operador.Id, comercio.Id);
        var acceso = new AccesoUsuario(usuario.Id, operador.Id, relacion.Id);

        db.AddRange(operador, comercio, usuario, relacion, acceso);
        await db.SaveChangesAsync(cancellationToken);
    }
}
