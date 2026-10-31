using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
namespace NexoRuta.IntegrationTests.Fixtures;
internal static class RepartoHttpData
{
    internal static readonly DateOnly FechaOperacion = DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo")).DateTime);
    internal static DateTimeOffset LlegadaEstimada => new(FechaOperacion.ToDateTime(new TimeOnly(15, 0)), TimeSpan.Zero);
    internal static async Task<Preparado> Preparar(IntegrationDatabase data, HttpClient client, bool politica = true, bool confirmar = true,
        bool bloquearDiscrepanciasCarga = true, bool recibir = true)
    {
        await using var db = data.CreateDbContext();
        var operador = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        var destinatario = new Destinatario(operador.OperadorId!.Value, "Destinatario de prueba", "destinatario@example.test");
        var direccion = new Direccion(operador.OperadorId.Value, "Av. de prueba 123");
        var envio = new Envio(operador.OperadorId.Value, comercio.ComercioId!.Value, comercio.UsuarioId, destinatario.Id, direccion.Id, (await db.ReglasOperativasOperadores.SingleAsync(r => r.OperadorId == operador.OperadorId)).VersionVigenteId!.Value);
        db.Destinatarios.Add(destinatario); db.Direcciones.Add(direccion); db.Envios.Add(envio);
        db.Bultos.Add(new Bulto(operador.OperadorId.Value, envio.Id, "RUTA-TEST", 1000, 10, 10, 10));
        await db.SaveChangesAsync();
        await client.SetBearerForAccessAsync(data, comercio.Id);
        if (confirmar)
        {
            using var destino = await client.PutAsJsonAsync($"/api/envios/{envio.Id}/destino", new { Version = 0, Latitud = -34.9, Longitud = -56.2 });
            Assert.Equal(HttpStatusCode.NoContent, destino.StatusCode);
        }
        await client.SetBearerForAccessAsync(data, operador.Id);
        using var perfil = await client.PutAsJsonAsync($"/api/repartidores/{operador.Id}", new { Nombre = "Repartidor de prueba", Activo = true });
        Assert.Equal(HttpStatusCode.OK, perfil.StatusCode);
        if (politica) { using var config = await client.PutAsJsonAsync("/api/reparto/configuracion", Politica(bloquearDiscrepanciasCarga)); Assert.Equal(HttpStatusCode.OK, config.StatusCode); }
        using var vehiculo = await client.PutAsJsonAsync("/api/reparto/vehiculos", new { Matricula = "SAB1234", PesoMaximoGramos = 10000000m, VolumenMaximoCm3 = 100000000000m, Activo = true });
        Assert.Equal(HttpStatusCode.OK, vehiculo.StatusCode);
        var idVehiculo = (await vehiculo.Content.ReadFromJsonAsync<VehiculoContract>())!.Id;
        if (recibir)
        {
            using var recepcion = await client.PostAsJsonAsync($"/api/deposito/envios/{envio.Id}/recepcion", new { Version = 0 });
            Assert.Equal(HttpStatusCode.NoContent, recepcion.StatusCode);
        }
        return new(operador.Id, idVehiculo, envio.Id);
    }
    internal static object Politica(bool bloquearDiscrepanciasCarga = true) => new { Version = 0, ZonaHoraria = "America/Montevideo", MaximoParadas = 30,
        BloquearDiscrepanciasCarga = bloquearDiscrepanciasCarga };
    internal static object Solicitud(Preparado p) => new { RepartidorAccesoId = p.AccesoId, p.VehiculoId, Fecha = FechaOperacion,
        Paradas = new[] { new { p.EnvioId, LlegadaEstimadaUtc = LlegadaEstimada } } };
    internal sealed record Preparado(Guid AccesoId, Guid VehiculoId, Guid EnvioId);
}
