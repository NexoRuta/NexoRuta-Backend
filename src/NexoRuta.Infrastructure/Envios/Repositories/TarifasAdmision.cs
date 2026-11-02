using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Infrastructure.Envios.Repositories;

internal sealed record OfertaAdmision(string ZonaNombre, long VersionZona, int RevisionZona, TarifaCalculo Tarifa);
internal static class TarifasAdmision
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    internal static async Task<OfertaAdmision[]> OfertasAsync(NexoRutaDbContext db, Guid operador,
        double latitud, double longitud, CancellationToken ct, Guid? version = null)
    {
        if (!double.IsFinite(latitud) || !double.IsFinite(longitud) || latitud is < -90 or > 90 || longitud is < -180 or > 180)
            throw new ArgumentException("Indicá una ubicación válida para consultar cobertura.");
        string json;
        try { json = await db.Database.SqlQuery<string>($"SELECT nexoruta_app.shipment_service_offers({operador},{latitud},{longitud},{version}) AS \"Value\"").SingleAsync(ct); }
        catch (Npgsql.PostgresException error) when (error.SqlState == "NXR01")
        { throw new ArgumentException("El operador elegido no está disponible."); }
        return JsonSerializer.Deserialize<OfertaAdmision[]>(json, Json) ?? [];
    }
    internal static async Task<ResultadoCalculoTarifa> CotizarAsync(NexoRutaDbContext db, Guid operador,
        ServicioEnvioDeclarado servicio, IReadOnlyList<Bulto> bultos, CancellationToken ct, Guid? version = null)
    {
        if (servicio is null || servicio.Modalidad is not ("Estandar" or "Urgente"))
            throw new ArgumentException("Seleccioná una zona y modalidad de servicio.");
        var ofertas = await OfertasAsync(db, operador, servicio.Latitud, servicio.Longitud, ct, version);
        var candidatas = ofertas.Where(o => o.Tarifa.Modalidad.ToString() == servicio.Modalidad).ToArray();
        var oferta = servicio.ZonaId == Guid.Empty
            ? (candidatas.Length == 1 ? candidatas[0] : null)
            : candidatas.SingleOrDefault(o => o.Tarifa.ZonaId == servicio.ZonaId);
        if (oferta is null) throw new ArgumentException("El operador no ofrece una tarifa inequívoca para la zona, modalidad y destino elegidos. Revisá la cobertura y seleccioná una zona válida.");
        if (servicio.VersionTarifarioEsperada is { } esperada && esperada != oferta.Tarifa.VersionTarifarioId)
            throw new NexoRuta.Application.Reparto.Exceptions.ConflictoRepartoException("La tarifa publicada cambió. Volvé a consultar el importe antes de crear el envío.");
        return Calcular(oferta.Tarifa, bultos);
    }
    internal static ResultadoCalculoTarifa Calcular(TarifaCalculo tarifa, IReadOnlyList<Bulto> bultos)
    {
        try { return CalculadoraTarifas.Calcular(tarifa, bultos.Select(b => new BultoCalculo(b.PesoGramos,
            b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros)).ToArray()); }
        catch (OverflowException) { throw new ArgumentException("El conjunto de medidas excede el límite de cálculo de la tarifa."); }
    }
    internal static Task GuardarAsync(NexoRutaDbContext db, Envio envio, ServicioEnvioDeclarado servicio,
        ResultadoCalculoTarifa cargo, CancellationToken ct) => GuardarAsync(db, envio.OperadorId, envio.Id, servicio, cargo, ct);
    internal static async Task GuardarAsync(NexoRutaDbContext db, Guid operador, Guid envio, ServicioEnvioDeclarado servicio,
        ResultadoCalculoTarifa cargo, CancellationToken ct)
    {
        servicio = servicio with { ZonaId = cargo.ZonaId };
        var oferta = (await OfertasAsync(db, operador, servicio.Latitud, servicio.Longitud, ct, cargo.VersionTarifarioId))
            .Single(o => o.Tarifa.ZonaId == servicio.ZonaId && o.Tarifa.Modalidad.ToString() == servicio.Modalidad);
        var cobertura = JsonSerializer.Serialize(new { servicio.ZonaId, oferta.VersionZona, oferta.RevisionZona, servicio.Latitud, servicio.Longitud }, Json);
        var id = Guid.CreateVersion7(); var solicitud = JsonSerializer.Serialize(servicio, Json); var resultado = JsonSerializer.Serialize(cargo, Json);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.\"TarifasEnvio\" (\"Id\",\"OperadorId\",\"EnvioId\",\"VersionTarifarioId\",\"Servicio\",\"Resultado\",\"Cobertura\") VALUES ({id},{operador},{envio},{cargo.VersionTarifarioId},{solicitud}::jsonb,{resultado}::jsonb,{cobertura}::jsonb)", ct);
    }
}
