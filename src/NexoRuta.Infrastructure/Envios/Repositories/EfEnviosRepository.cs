using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using System.Text.Json;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Domain.Administracion;
using Npgsql;

namespace NexoRuta.Infrastructure.Envios.Repositories;

public sealed class EfEnviosRepository(NexoRutaDbContext db) : IEnviosRepository
{
    public async Task GuardarAsync(
        Envio envio,
        Destinatario destinatario,
        Direccion direccion,
        Bulto bulto,
        CancellationToken cancellationToken = default)
    {
        if (destinatario.OperadorId != envio.OperadorId || direccion.OperadorId != envio.OperadorId ||
            bulto.OperadorId != envio.OperadorId || bulto.EnvioId != envio.Id)
            throw new ArgumentException("Shipment aggregate tenant keys must refer to the same shipment and operator.");
        await using var transaction = await db.BeginOrJoinAsync(cancellationToken);
        // Keep the commerce lock on the API path; the scoped SECURITY DEFINER insert locks only the
        // chosen operator because commerce SELECT cannot lock inactive operators under RLS.
        var commerce = await db.Comercios.FromSqlInterpolated(
            $"SELECT * FROM \"Comercios\" WHERE \"Id\" = {envio.ComercioId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var operatorEntity = await db.Operadores.SingleOrDefaultAsync(x => x.Id == envio.OperadorId, cancellationToken);
        if (commerce is null || operatorEntity is null)
            throw new OrganizacionSuspendidaException("No se puede crear el envío porque una organización ya no está disponible.");
        if (commerce.Estado != EstadoOrganizacion.Activa || operatorEntity.Estado != EstadoOrganizacion.Activa)
            throw new OrganizacionSuspendidaException("No se puede crear el envío mientras una organización esté suspendida.");

        await db.SetShipmentAsync(envio.OperadorId, envio.Id, destinatario.Id, direccion.Id, bulto.Id, cancellationToken);

        // Trabajo asíncrono: generar el seguimiento.
        var trabajoSeguimiento = OutboxMessage.ForShipment(
            operatorId: envio.OperadorId,
            shipmentId: envio.Id,
            type: OutboxMessageTypes.TrackingRequested,
            destination: OutboxDestino.RabbitMq,
            payload: JsonSerializer.Serialize(new
            {
                envioId = envio.Id,
                operadorId = envio.OperadorId
            }));

        // Evento de negocio: informar que se creó un envío.
        var eventoEnvioCreado = OutboxMessage.ForShipment(
            operatorId: envio.OperadorId,
            shipmentId: envio.Id,
            type: OutboxMessageTypes.ShipmentCreated,
            destination: OutboxDestino.Kafka,
            payload: JsonSerializer.Serialize(new
            {
                envioId = envio.Id,
                operadorId = envio.OperadorId,
                comercioId = envio.ComercioId,
                estado = envio.Estado.ToString()
            }));

        // EF Core guarda todo en la misma operación.
        Guid createdId;
        try
        {
            createdId = await db.Database.SqlQueryRaw<Guid>(
                "SELECT " + RlsSqlFunctionContract.CreateShipment +
                "({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, {9}, {10}) AS \"Value\"",
                envio.Id, destinatario.Id, direccion.Id, bulto.Id,
                destinatario.Nombre, direccion.Descripcion, bulto.Codigo,
                bulto.PesoGramos, bulto.LargoCentimetros, bulto.AnchoCentimetros, bulto.AltoCentimetros)
                .SingleAsync(cancellationToken);
        }
        catch (PostgresException error) when (error.SqlState == "NXR01")
        {
            throw new OrganizacionSuspendidaException("No se puede crear el envío mientras el operador seleccionado está suspendido.");
        }
        if (createdId != envio.Id)
            throw new InvalidOperationException("The database did not create the requested shipment.");

        db.AddRange(trabajoSeguimiento, eventoEnvioCreado);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EnvioDetalle>> ListarAsync(
        Guid? operadorId,
        Guid? comercioId,
        CancellationToken cancellationToken = default)
    {
        var shipments = await (
            from envio in db.Envios
            join comercio in db.Comercios on envio.ComercioId equals comercio.Id
            join selectedOperator in db.Operadores on envio.OperadorId equals selectedOperator.Id
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
                OperadorNombre = selectedOperator.Nombre,
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
            .Select(x => new
            {
                x.EnvioId,
                Detail = new BultoDetalle(
                Codigo: x.Codigo, PesoGramos: x.PesoGramos, LargoCentimetros: x.LargoCentimetros,
                AnchoCentimetros: x.AnchoCentimetros, AltoCentimetros: x.AltoCentimetros)
            })
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
