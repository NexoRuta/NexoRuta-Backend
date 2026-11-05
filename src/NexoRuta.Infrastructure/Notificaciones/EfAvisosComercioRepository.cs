using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Notificaciones.Repositories;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Notificaciones;
public sealed class EfAvisosComercioRepository(NexoRutaDbContext db) : IAvisosComercioRepository
{
    public async Task<IReadOnlyList<AvisoComercioDatos>> ListarAsync(CancellationToken ct) =>
        await db.Database.SqlQuery<AvisoComercioDatos>($"""
        SELECT "EventoId","EnvioId","Tipo","VersionContrato","Datos"::text AS "Datos","RecibidoEnUtc"
        FROM public."AvisosComercio" ORDER BY "RecibidoEnUtc" DESC,"EventoId" DESC LIMIT 200
        """).ToArrayAsync(ct);
}
