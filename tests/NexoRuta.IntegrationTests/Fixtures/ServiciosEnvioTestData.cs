using Microsoft.EntityFrameworkCore;
using NetTopologySuite;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.IntegrationTests.Fixtures;

// Explicit synthetic shipping scenario, opted into by shipping tests only.
// Does not run in application bootstrap or change an existing tariff/zone.
internal static class ServiciosEnvioTestData
{
    public static ServicioEnvioRequest Declarar(Guid? operador) => new()
        { ZonaId = operador, Latitud = -34.901, Longitud = -56.201, Modalidad = "Estandar" };
    public static ServicioEnvioDeclarado Comando(Guid operador) => new(operador, -34.901, -56.201, "Estandar");
    public static async Task SembrarAsync(NexoRutaDbContext db)
    {
        var actores = await db.AccesosUsuario.AsNoTracking().Where(a => a.OperadorId != null)
            .Select(a => new { Operador = a.OperadorId!.Value, a.UsuarioId }).ToArrayAsync();
        foreach (var actor in actores.DistinctBy(a => a.Operador)) await PublicarServicioAsync(db, actor.Operador, actor.UsuarioId);
    }
    public static async Task<Guid> PublicarReglasYServicioAsync(NexoRutaDbContext db, Guid operador, Guid usuario)
    {
        var reglas = await ReglasEnvioTestData.PublicarInicialAsync(db, operador, usuario);
        await PublicarServicioAsync(db, operador, usuario);
        return reglas;
    }
    public static async Task<Guid> PublicarServicioAsync(NexoRutaDbContext db, Guid operador, Guid usuario,
        decimal importeBase = 100m, decimal porKg = 10m, decimal porM3 = 200m)
    {
        await using var tx = await db.BeginOrJoinAsync();
        var root = await db.TarifariosOperador.SingleOrDefaultAsync(t => t.OperadorId == operador);
        if (root?.VersionVigenteId is { } id) { await tx.CommitAsync(); return id; }
        var zona = await db.ZonasCobertura.SingleOrDefaultAsync(z => z.OperadorId == operador && z.Id == operador);
        if (zona is null)
        {
            var gf = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
            var polygon = gf.CreateMultiPolygon([gf.CreatePolygon([new(-56.4,-35),new(-56,-35),new(-56,-34.7),new(-56.4,-34.7),new(-56.4,-35)])]);
            zona = ZonaCobertura.Crear(operador,"Cobertura sintética Montevideo",polygon,id:operador,
                franjasIniciales:[new(DiaSemanaIso.Lunes,new TimeOnly(9,0),new TimeOnly(18,0))]);
            zona.Activar(zona.Version); db.ZonasCobertura.Add(zona); await db.SaveChangesAsync();
        }
        if (root is null) { root=TarifarioOperador.Crear(operador); db.TarifariosOperador.Add(root); await db.SaveChangesAsync(); }
        var version = VersionTarifario.CrearBorrador(operador,usuario);
        db.VersionesTarifario.Add(version); await db.SaveChangesAsync();
        version.ReemplazarConfiguracion(0,[new(operador,ModalidadTarifaria.Estandar,importeBase,porKg,porM3,[])]);
        await db.SaveChangesAsync(); version.Publicar(version.VersionConcurrencia,root.UltimoNumeroPublicacion+1,usuario,new HashSet<Guid>{operador});
        await db.SaveChangesAsync(); root.Publicar(version); await db.SaveChangesAsync(); await tx.CommitAsync(); return version.Id;
    }
}
