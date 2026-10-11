using Microsoft.EntityFrameworkCore;

using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Domain.Seguimiento;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Seguimiento.Repositories;

public sealed class EfEnlacesSeguimientoRepository(
    NexoRutaDbContext db
) : IEnlacesSeguimientoRepository
{
    public async Task GuardarAsync(
        EnlaceSeguimiento enlace,
        CancellationToken cancellationToken = default)
    {
        db.EnlacesSeguimiento.Add(enlace);

        await db.SaveChangesAsync(cancellationToken);
    }


    public async Task<EnlaceSeguimiento?> BuscarPorHashAsync(
    string tokenHash,
    CancellationToken cancellationToken = default)
    {
        return await db.EnlacesSeguimiento
            .AsNoTracking()
            .SingleOrDefaultAsync(
                enlace => enlace.TokenHash == tokenHash,
                cancellationToken);
    }
}