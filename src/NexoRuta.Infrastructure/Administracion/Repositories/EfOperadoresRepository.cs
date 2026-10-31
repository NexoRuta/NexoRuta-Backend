using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfOperadoresRepository(NexoRutaDbContext db) : IOperadoresRepository
{
    public async Task<IReadOnlyList<OperadorDisponible>> ListarAsync(CancellationToken cancellationToken = default)
        => await db.Operadores.Where(x => x.Estado == EstadoOrganizacion.Activa).OrderBy(x => x.Nombre)
            .Select(x => new OperadorDisponible(x.Id, x.Nombre))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<OperadorDisponibleParaImportacion>> ListarParaImportacionAsync(
        CancellationToken cancellationToken = default)
    {
        var active = (await ListarAsync(cancellationToken))
            .Select(operatorInfo => new OperadorDisponibleParaImportacion(operatorInfo.OperadorId, operatorInfo.Nombre, true))
            .ToList();
        var activeIds = active.Select(operatorInfo => operatorInfo.OperadorId).ToHashSet();
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
            await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"OperadorId\", \"Nombre\" FROM nexoruta_app.list_import_operator_history()";
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var operatorId = reader.GetGuid(0);
                if (!activeIds.Contains(operatorId))
                    active.Add(new OperadorDisponibleParaImportacion(operatorId, reader.GetString(1), false));
            }
        }
        finally
        {
            if (openedHere)
                await db.Database.CloseConnectionAsync();
        }

        return active.OrderBy(operatorInfo => operatorInfo.Nombre, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<OperadorDisponible?> ObtenerAsync(Guid operadorId, CancellationToken cancellationToken = default)
        => db.Operadores.Where(x => x.Id == operadorId && x.Estado == EstadoOrganizacion.Activa)
            .Select(x => new OperadorDisponible(x.Id, x.Nombre))
            .SingleOrDefaultAsync(cancellationToken);
}
