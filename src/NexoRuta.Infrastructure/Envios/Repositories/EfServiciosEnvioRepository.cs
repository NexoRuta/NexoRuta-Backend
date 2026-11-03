using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Envios.Repositories;
public sealed class EfServiciosEnvioRepository(NexoRutaDbContext db, IUsuarioActual usuario) : IServiciosEnvioRepository
{
    public async Task<IReadOnlyList<CoberturaEnvioDisponible>> CoberturaAsync(Guid operador, CancellationToken ct)
    {
        (await usuario.ObtenerAsync(ct)).RequerirComercio();
        try
        {
            var json = await db.Database.SqlQuery<string>($"SELECT nexoruta_app.shipment_coverage({operador}) AS \"Value\"").SingleAsync(ct);
            return JsonSerializer.Deserialize<CoberturaEnvioDisponible[]>(json, TarifasAdmision.Json) ?? [];
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "NXR01")
        { throw new ArgumentException("El operador elegido no est\u00e1 disponible."); }
    }
    public async Task<IReadOnlyList<ServicioEnvioDisponible>> ListarAsync(Guid operador, double latitud, double longitud, CancellationToken ct)
    {
        (await usuario.ObtenerAsync(ct)).RequerirComercio();
        return (await TarifasAdmision.OfertasAsync(db, operador, latitud, longitud, ct))
            .Select(o => new ServicioEnvioDisponible(o.Tarifa.ZonaId, o.ZonaNombre, o.Tarifa.Modalidad.ToString())).ToArray();
    }
    public async Task<ResultadoCalculoTarifa> CotizarAsync(Guid operador,
        NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado servicio,
        IReadOnlyList<NexoRuta.Application.Envios.Commands.BultoDeclarado> bultos, CancellationToken ct)
    {
        (await usuario.ObtenerAsync(ct)).RequerirComercio();
        if (bultos.Count == 0) throw new ArgumentException("Declarar al menos un bulto.");
        var envio = Guid.CreateVersion7();
        var declarados = bultos.Select(b => new NexoRuta.Domain.Envios.Bulto(operador, envio, b.Codigo,
            b.PesoGramos, b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros)).ToArray();
        if (declarados.Select(b => b.Codigo).Distinct(StringComparer.Ordinal).Count() != declarados.Length)
            throw new ArgumentException("Los códigos de bulto no pueden repetirse.");
        return await TarifasAdmision.CotizarAsync(db, operador, servicio, declarados, ct);
    }
    public async Task<IReadOnlyList<TarifaCargoRegistrado>> HistorialAsync(Guid envio, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<CargoPersistido>($"SELECT \"Id\",\"CreadoEnUtc\",\"Resultado\"::text AS \"Resultado\",\"Servicio\"::text AS \"Servicio\",\"Cobertura\"::text AS \"Cobertura\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\"={envio} ORDER BY \"CreadoEnUtc\",\"Id\"").ToArrayAsync(ct);
        return rows.Select(row =>
        {
            var result = JsonSerializer.Deserialize<ResultadoCalculoTarifa>(row.Resultado, TarifasAdmision.Json)!;
            var service = JsonSerializer.Deserialize<NexoRuta.Application.Envios.Commands.ServicioEnvioDeclarado>(row.Servicio, TarifasAdmision.Json)!;
            using var coverage = JsonDocument.Parse(row.Cobertura);
            return new TarifaCargoRegistrado(row.Id, row.CreadoEnUtc, result, service,
                coverage.RootElement.GetProperty("VersionZona").GetInt64(), coverage.RootElement.GetProperty("RevisionZona").GetInt32());
        }).ToArray();
    }
    private sealed class CargoPersistido
    {
        public Guid Id { get; set; }
        public DateTimeOffset CreadoEnUtc { get; set; }
        public string Resultado { get; set; } = "";
        public string Servicio { get; set; } = "";
        public string Cobertura { get; set; } = "";
    }
    public async Task<ResultadoCalculoTarifa?> ObtenerCargoAsync(Guid envio, CancellationToken ct)
    {
        var json = await db.Database.SqlQuery<string>($"SELECT \"Resultado\"::text AS \"Value\" FROM public.\"TarifasEnvio\" WHERE \"EnvioId\"={envio} ORDER BY \"CreadoEnUtc\" DESC,\"Id\" DESC LIMIT 1").SingleOrDefaultAsync(ct);
        return json is null ? null : JsonSerializer.Deserialize<ResultadoCalculoTarifa>(json, TarifasAdmision.Json);
    }
}
