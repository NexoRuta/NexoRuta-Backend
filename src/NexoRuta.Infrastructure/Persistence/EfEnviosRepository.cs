using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class EfEnviosRepository(NexoRutaDbContext db) : IEnviosRepository
{
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
        Guid? operadorId,
        Guid? comercioId,
        CancellationToken cancellationToken = default)
    {
        var shipments = await (
            from envio in db.Envios
            join comercio in db.Comercios on envio.ComercioId equals comercio.Id
            join operador in db.Operadores on envio.OperadorId equals operador.Id
            join usuario in db.Usuarios on envio.CreadoPorUsuarioId equals usuario.Id
            join destinatario in db.Destinatarios on envio.DestinatarioId equals destinatario.Id
            join direccion in db.Direcciones on envio.DireccionId equals direccion.Id
            where (operadorId != null && envio.OperadorId == operadorId)
                || (comercioId != null && envio.ComercioId == comercioId)
            orderby envio.Id descending
            select new
            {
                envio.Id,
                envio.OperadorId,
                envio.ComercioId,
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
            .Where(x => ids.Contains(x.EnvioId))
            .Select(x => new { x.EnvioId, Detail = new BultoDetalle(
                Codigo: x.Codigo, PesoGramos: x.PesoGramos, LargoCentimetros: x.LargoCentimetros,
                AnchoCentimetros: x.AnchoCentimetros, AltoCentimetros: x.AltoCentimetros) })
            .ToListAsync(cancellationToken);
        var packagesByShipment = packages.ToLookup(x => x.EnvioId, x => x.Detail);

        return shipments.Select(x => new EnvioDetalle(
            Id: x.Id,
            Origen: new OrigenEnvio(
                OperadorId: x.OperadorId,
                ComercioId: x.ComercioId,
                CreadoPorUsuarioId: x.CreadoPorUsuarioId,
                UsuarioEmail: x.UsuarioEmail,
                OperadorNombre: x.OperadorNombre,
                ComercioNombre: x.ComercioNombre),
            DestinatarioNombre: x.DestinatarioNombre,
            Direccion: x.Direccion,
            Estado: x.Estado,
            Bultos: packagesByShipment[x.Id].ToArray())).ToArray();
    }
}
