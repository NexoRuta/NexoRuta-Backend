using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion;

public sealed class EfComerciosRepository(NexoRutaDbContext db) : IComerciosRepository
{
    public async Task<IReadOnlyList<ComercioResumen>> ListarAsync(CancellationToken cancellationToken = default)
        => await db.Comercios.OrderBy(x => x.Nombre)
            .Select(x => new ComercioResumen(x.Id, x.Nombre))
            .ToListAsync(cancellationToken);

    public Task<ComercioResumen?> ObtenerAsync(Guid comercioId, CancellationToken cancellationToken = default)
        => db.Comercios.Where(x => x.Id == comercioId)
            .Select(x => new ComercioResumen(x.Id, x.Nombre))
            .SingleOrDefaultAsync(cancellationToken);
}
