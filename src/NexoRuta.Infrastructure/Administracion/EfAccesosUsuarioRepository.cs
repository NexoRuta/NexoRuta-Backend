using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Administracion;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Administracion;

public sealed class EfAccesosUsuarioRepository(NexoRutaDbContext db) : IAccesosUsuarioRepository
{
    private IQueryable<ContextoUsuario> Consulta(IQueryable<AccesoUsuario> accesos) =>
        from acceso in accesos
        join usuario in db.Usuarios on acceso.UsuarioId equals usuario.Id
        join operador in db.Operadores on acceso.OperadorId equals (Guid?)operador.Id into operadores
        from operador in operadores.DefaultIfEmpty()
        join comercio in db.Comercios on acceso.ComercioId equals (Guid?)comercio.Id into comercios
        from comercio in comercios.DefaultIfEmpty()
        orderby usuario.Email, operador.Nombre, comercio.Nombre
        select new ContextoUsuario(
            acceso.Id, usuario.Id, acceso.OperadorId, acceso.ComercioId,
            usuario.Email, operador == null ? null : operador.Nombre,
            comercio == null ? null : comercio.Nombre, acceso.EsPropietario);

    public async Task<IReadOnlyList<ContextoUsuario>> ListarAsync(
        TipoAccesoUsuario tipo, CancellationToken cancellationToken = default)
        => await Consulta(db.AccesosUsuario.Where(x => tipo == TipoAccesoUsuario.Operador
                ? x.OperadorId != null
                : x.ComercioId != null))
            .ToListAsync(cancellationToken);

    public Task<ContextoUsuario?> ObtenerAsync(Guid accesoId, CancellationToken cancellationToken = default)
        => Consulta(db.AccesosUsuario.Where(x => x.Id == accesoId)).SingleOrDefaultAsync(cancellationToken);
}
