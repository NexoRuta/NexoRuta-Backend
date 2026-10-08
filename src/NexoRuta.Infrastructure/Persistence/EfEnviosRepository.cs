using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class EfEnviosRepository(NexoRutaDbContext db) : IEnviosRepository
{
    public async Task<DemoComercioContext?> ObtenerContextoDemoAsync(CancellationToken cancellationToken = default)
    {
        return await (
            from acceso in db.AccesosUsuario
            join usuario in db.Usuarios on acceso.UsuarioId equals usuario.Id
            join relacion in db.OperadoresComercios on acceso.OperadorComercioId equals relacion.Id
            join operador in db.Operadores on acceso.OperadorId equals operador.Id
            join comercio in db.Comercios on relacion.ComercioId equals comercio.Id
            where usuario.Email == DemoDataSeeder.UsuarioEmail
                && relacion.OperadorId == acceso.OperadorId
            select new DemoComercioContext(
                usuario.Id,
                operador.Id,
                relacion.Id,
                usuario.Email,
                operador.Nombre,
                comercio.Nombre))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        Bulto bulto,
        CancellationToken cancellationToken = default)
    {
        db.AddRange(destinatario, direccion, envio, bulto);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
        DemoComercioContext contexto,
        CancellationToken cancellationToken = default)
    {
        var shipments = await (
            from envio in db.Envios
            join relacion in db.OperadoresComercios on envio.OperadorComercioId equals relacion.Id
            join comercio in db.Comercios on relacion.ComercioId equals comercio.Id
            join operador in db.Operadores on envio.OperadorId equals operador.Id
            join usuario in db.Usuarios on envio.CreadoPorUsuarioId equals usuario.Id
            join destinatario in db.Destinatarios on envio.DestinatarioId equals destinatario.Id
            join direccion in db.Direcciones on envio.DireccionId equals direccion.Id
            where envio.OperadorId == contexto.OperadorId
                && envio.OperadorComercioId == contexto.OperadorComercioId
            orderby envio.Id descending
            select new
            {
                envio.Id,
                envio.OperadorId,
                envio.OperadorComercioId,
                envio.CreadoPorUsuarioId,
                UsuarioEmail = usuario.Email,
                OperadorNombre = operador.Nombre,
                ComercioNombre = comercio.Nombre,
                DestinatarioNombre = destinatario.Nombre,
                Direccion = direccion.Descripcion,
                Estado = envio.Estado.ToString()
            }).ToListAsync(cancellationToken);

        if (shipments.Count == 0)
            return [];

        var ids = shipments.Select(x => x.Id).ToArray();
        var packages = await db.Bultos
            .Where(x => x.OperadorId == contexto.OperadorId && ids.Contains(x.EnvioId))
            .Select(x => new { x.EnvioId, Detail = new BultoDetalle(
                x.Codigo, x.PesoGramos, x.LargoCentimetros, x.AnchoCentimetros, x.AltoCentimetros) })
            .ToListAsync(cancellationToken);
        var packagesByShipment = packages.ToLookup(x => x.EnvioId, x => x.Detail);

        return shipments.Select(x => new EnvioDetalle(
            x.Id,
            x.OperadorId,
            x.OperadorComercioId,
            x.CreadoPorUsuarioId,
            x.UsuarioEmail,
            x.OperadorNombre,
            x.ComercioNombre,
            x.DestinatarioNombre,
            x.Direccion,
            x.Estado,
            packagesByShipment[x.Id].ToArray())).ToArray();
    }
}
