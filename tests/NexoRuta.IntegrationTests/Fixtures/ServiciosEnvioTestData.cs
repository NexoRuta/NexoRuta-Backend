using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexoRuta.Domain.Envios;
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
        decimal importeBase = 100m, decimal porKg = 10m, decimal porM3 = 200m, bool incluirUrgente = false)
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
        ModalidadTarifaria[] modalidades = incluirUrgente ? [ModalidadTarifaria.Estandar, ModalidadTarifaria.Urgente] : [ModalidadTarifaria.Estandar];
        version.ReemplazarConfiguracion(0, modalidades.Select(modalidad => new TarifaZonaModalidadConfiguracion(operador, modalidad, importeBase, porKg, porM3, [])));
        await db.SaveChangesAsync(); version.Publicar(version.VersionConcurrencia,root.UltimoNumeroPublicacion+1,usuario,new HashSet<Guid>{operador});
        await db.SaveChangesAsync(); root.Publicar(version); await db.SaveChangesAsync(); await tx.CommitAsync(); return version.Id;
    }

    // Explicit fixture setup for tests of routing; production admission is tested separately under restricted roles.
    public static async Task AdjuntarCargoAsync(NexoRutaDbContext db, Envio envio, IReadOnlyList<Bulto> bultos)
    {
        var usuario = await db.AccesosUsuario.Where(a => a.OperadorId == envio.OperadorId).Select(a => a.UsuarioId).FirstAsync();
        var versionId = await PublicarServicioAsync(db, envio.OperadorId, usuario);
        var version = await db.VersionesTarifario.SingleAsync(v => v.Id == versionId);
        var tarifa = await db.TarifasZonaModalidad.SingleAsync(t => t.OperadorId == envio.OperadorId
            && t.VersionId == versionId && t.ZonaId == envio.OperadorId && t.Modalidad == ModalidadTarifaria.Estandar);
        var zona = await db.ZonasCobertura.SingleAsync(z => z.Id == envio.OperadorId);
        var calculo = CalculadoraTarifas.Calcular(new(envio.OperadorId, ModalidadTarifaria.Estandar,
            tarifa.ImporteBaseUyu, tarifa.PrecioPorKilogramoUyu, tarifa.PrecioPorMetroCubicoUyu, [],
            version.Id, version.NumeroVersionPublicada, version.VersionConcurrencia),
            bultos.Select(b => new BultoCalculo(b.PesoGramos, b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros)).ToArray());
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var servicio = JsonSerializer.Serialize(Comando(envio.OperadorId), json);
        var resultado = JsonSerializer.Serialize(calculo, json);
        var cobertura = JsonSerializer.Serialize(new { ZonaId = zona.Id, VersionZona = zona.Version, RevisionZona = zona.RevisionActual });
        var id = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.\"TarifasEnvio\" (\"Id\",\"OperadorId\",\"EnvioId\",\"VersionTarifarioId\",\"Servicio\",\"Resultado\",\"Cobertura\") VALUES ({id},{envio.OperadorId},{envio.Id},{versionId},{servicio}::jsonb,{resultado}::jsonb,{cobertura}::jsonb)");
    }
}
