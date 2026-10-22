using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion.Repositories;

public sealed class EfOrganizacionPropiaRepository(NexoRutaDbContext db) : IOrganizacionPropiaRepository
{
    public Task<OrganizacionPropia?> ObtenerAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => tipo switch
        {
            TipoAccesoUsuario.Comercio => db.Comercios.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new OrganizacionPropia(x.Id, x.Nombre, x.Version, x.Estado))
                .SingleOrDefaultAsync(ct),
            TipoAccesoUsuario.Operador => db.Operadores.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new OrganizacionPropia(x.Id, x.Nombre, x.Version, x.Estado))
                .SingleOrDefaultAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(tipo))
        };

    public Task<bool> TieneDuenoRegistradoAsync(TipoAccesoUsuario tipo, Guid id, CancellationToken ct)
        => db.AccesosUsuario.AsNoTracking().AnyAsync(x => x.TipoOrganizacion == tipo
            && x.OrganizacionId == id && x.EsPropietario, ct);

    public async Task<OrganizacionPropia?> ActualizarNombreAsync(
        TipoAccesoUsuario tipo, Guid id, string nombre, long version, CancellationToken ct)
    {
        if (tipo == TipoAccesoUsuario.Comercio)
        {
            var comercio = await db.Comercios.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (comercio is null) return null;
            db.Entry(comercio).Property(x => x.Version).OriginalValue = version;
            comercio.ActualizarNombre(nombre);
            await db.SaveChangesAsync(ct);
            return new(comercio.Id, comercio.Nombre, comercio.Version, comercio.Estado);
        }

        if (tipo == TipoAccesoUsuario.Operador)
        {
            var operador = await db.Operadores.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (operador is null) return null;
            db.Entry(operador).Property(x => x.Version).OriginalValue = version;
            operador.ActualizarNombre(nombre);
            await db.SaveChangesAsync(ct);
            return new(operador.Id, operador.Nombre, operador.Version, operador.Estado);
        }

        throw new ArgumentOutOfRangeException(nameof(tipo));
    }
}
