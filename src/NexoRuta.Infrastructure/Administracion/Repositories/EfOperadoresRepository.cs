using Microsoft.EntityFrameworkCore;
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

    public Task<OperadorDisponible?> ObtenerAsync(Guid operadorId, CancellationToken cancellationToken = default)
        => db.Operadores.Where(x => x.Id == operadorId && x.Estado == EstadoOrganizacion.Activa)
            .Select(x => new OperadorDisponible(x.Id, x.Nombre))
            .SingleOrDefaultAsync(cancellationToken);
}
