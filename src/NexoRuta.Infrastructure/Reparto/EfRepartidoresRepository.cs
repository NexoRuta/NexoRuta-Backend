using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.Infrastructure.Reparto;

public sealed class EfRepartidoresRepository(NexoRutaDbContext db) : IRepartidoresRepository
{
    private IQueryable<RepartidorResumen> Consulta(Guid operadorId, Guid? accesoId = null, bool soloActivos = false) =>
        from perfil in db.Repartidores.AsNoTracking()
        join acceso in db.AccesosUsuario on perfil.AccesoId equals acceso.Id
        join usuario in db.Usuarios on acceso.UsuarioId equals usuario.Id
        where perfil.OperadorId == operadorId && acceso.OperadorId == operadorId
            && (accesoId == null || perfil.AccesoId == accesoId)
            && (!soloActivos || perfil.Activo && acceso.Activo && acceso.RolId != null)
        orderby perfil.Nombre, perfil.AccesoId
        select new RepartidorResumen(perfil.AccesoId, usuario.Id, perfil.OperadorId, usuario.Email,
            perfil.Nombre, perfil.Activo && acceso.Activo && acceso.RolId != null, perfil.Version);

    public async Task<IReadOnlyList<RepartidorResumen>> ListarAsync(Guid operadorId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ContextoAsync(operadorId, ct);
        var datos = await Consulta(operadorId).ToArrayAsync(ct);
        await tx.CommitAsync(ct);
        return datos;
    }

    public async Task<RepartidorResumen?> ObtenerActivoAsync(Guid operadorId, Guid accesoId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ContextoAsync(operadorId, ct);
        var datos = await Consulta(operadorId, accesoId, soloActivos: true).SingleOrDefaultAsync(ct);
        await tx.CommitAsync(ct);
        return datos;
    }

    public async Task<RepartidorResumen?> GuardarAsync(Guid operadorId, Guid accesoId, string nombre,
        bool activo, long? version, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await ContextoAsync(operadorId, ct);
        // Bloquea también la creación: dos altas del mismo acceso no compiten contra una fila inexistente.
        var acceso = await db.AccesosUsuario.FromSqlInterpolated(
            $"SELECT * FROM \"AccesosUsuario\" WHERE \"Id\" = {accesoId} AND \"OperadorId\" = {operadorId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (acceso is null || !acceso.Activo || acceso.RolId is null) return null;
        var perfil = await db.Repartidores.SingleOrDefaultAsync(x => x.AccesoId == accesoId && x.OperadorId == operadorId, ct);
        if (perfil is null)
        {
            if (version is not null) throw new ConflictoRepartoException("El perfil todavía no existe. Actualizá la lista.");
            perfil = new Repartidor(accesoId, operadorId, nombre);
            if (!activo) perfil.Actualizar(nombre, false);
            db.Repartidores.Add(perfil);
        }
        else
        {
            if (version != perfil.Version) throw new ConflictoRepartoException("El perfil cambió. Actualizá la lista antes de guardar.");
            perfil.Actualizar(nombre, activo);
        }
        await db.SaveChangesAsync(ct);
        var datos = await Consulta(operadorId, accesoId).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return datos;
    }

    private Task ContextoAsync(Guid operadorId, CancellationToken ct) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT set_config('nexoruta.operador_id', {operadorId.ToString()}, true)", ct);
}
