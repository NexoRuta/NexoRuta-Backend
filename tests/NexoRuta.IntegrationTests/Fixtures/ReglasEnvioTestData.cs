using Microsoft.EntityFrameworkCore;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Fixtures;

internal static class ReglasEnvioTestData
{
    public static async Task SembrarInicialesAsync(NexoRutaDbContext db)
    {
        var actores = await db.AccesosUsuario.AsNoTracking().Where(a => a.OperadorId != null)
            .Select(a => new { OperadorId = a.OperadorId!.Value, a.UsuarioId }).ToArrayAsync();
        foreach (var actor in actores.DistinctBy(a => a.OperadorId))
            await PublicarInicialAsync(db, actor.OperadorId, actor.UsuarioId);
    }

    public static async Task<Guid> PublicarInicialAsync(NexoRutaDbContext db, Guid operadorId, Guid usuarioId)
    {
        await using var tx = await db.BeginOrJoinAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Operadores\" WHERE \"Id\"={operadorId} FOR UPDATE");
        var root = await db.ReglasOperativasOperadores.SingleOrDefaultAsync(r => r.OperadorId == operadorId);
        if (root?.VersionVigenteId is { } publicada)
        {
            await tx.CommitAsync(); return publicada;
        }
        if (root is null)
        {
            root = ReglasOperativasOperador.Crear(operadorId);
            db.ReglasOperativasOperadores.Add(root); await db.SaveChangesAsync();
        }
        // Identificador estable de fixture; no hay configuración automática en código de producción.
        var version = VersionReglasOperativas.CrearBorrador(operadorId, usuarioId, id: operadorId);
        db.VersionesReglasOperativas.Add(version); await db.SaveChangesAsync();
        version.ReemplazarConfiguracion(0, new(3, 1, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,
            2880, 1440, [TipoPruebaEntrega.Fotografia, TipoPruebaEntrega.NombreReceptor],
            [new("AUSENTE", "Ausente", true)]));
        await db.SaveChangesAsync();
        version.Publicar(1, 1, usuarioId); await db.SaveChangesAsync();
        root.Publicar(version); await db.SaveChangesAsync();
        await tx.CommitAsync(); return version.Id;
    }
}
