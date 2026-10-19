using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.IntegrationTests.Seguimiento;
using NexoRuta.Worker;

namespace NexoRuta.IntegrationTests.Notificaciones;

public sealed partial class PushOutboxTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DestinatarioVerificadoNotificaAlActualYPreservaEntregaOffline(bool recibio)
    {
        using var p = await PrepararPush(1, registrarAviso: false);
        var hoja = await CargarParaCancelar(p); var parada = hoja.Paradas[0]; string token;
        await using (var db = p.Data.CreateDbContext())
            token = await SeguimientoTestData.CreateActiveLinkAsync(db, await db.Envios.SingleAsync(e => e.Id == parada.EnvioId));
        // El visitante no tiene sesión: conservar el cliente autenticado solo para el trabajo del repartidor.
        using var publico = p.Factory.CreateHttpClient();
        var url = $"/api/public/seguimiento/{token}/cancelacion";
        using var solicitar = await publico.PostAsync(url + "/codigo", null);
        Assert.Equal(HttpStatusCode.Accepted, solicitar.StatusCode);
        CorreoCodigoCancelacion comando;
        await using (var db = p.Data.CreateDbContext())
        {
            var payload = await db.OutboxMessages.Where(x => x.Tipo == OutboxMessageTypes.CancellationCodeEmail).Select(x => x.Payload).SingleAsync();
            var sobre = JsonDocument.Parse(payload).RootElement.GetProperty("protectedCommand").GetString()!;
            comando = JsonSerializer.Deserialize<CorreoCodigoCancelacion>(p.Factory.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(CorreoCodigoCancelacion.ProtectionPurpose).Unprotect(sobre))!;
        }
        using var cancelar = await publico.PostAsJsonAsync(url, new { comando.Codigo });
        Assert.Equal(HttpStatusCode.OK, cancelar.StatusCode);
        await using (var db = p.Data.CreateDbContext())
        {
            Assert.Equal(EstadoCustodiaBulto.Repartidor, (await db.Bultos.SingleAsync()).Custodia);
            Assert.False((await db.ParadasRuta.SingleAsync()).Activa);
            var aviso = Assert.Single(await db.AvisosReparto.ToArrayAsync());
            Assert.Equal((hoja.Id, hoja.RepartidorAccesoId), (aviso.RutaId, aviso.AccesoId));
            Assert.NotNull((await db.CancelacionesEnvio.SingleAsync()).AutorizacionPublicaId);
        }
        await using var services = await ServiciosWorker(p); var transporte = new Transporte();
        using var worker = new PushOutboxWorker(services.GetRequiredService<IServiceScopeFactory>(), transporte, NullLogger<PushOutboxWorker>.Instance);
        Assert.True(await worker.ProcesarSiguienteAsync(default)); Assert.Equal(p.Fids, Assert.Single(transporte.Enviados).Fids);
        var resultado = recibio
            ? await Operar(p.Client, hoja, 4, "EntregaFisica", parada.EnvioId, parada.EnvioVersion)
            : await Operar(p.Client, hoja, 4, "Rendicion", datos: new { BultoIds = new[] { parada.Bultos[0].Id } });
        Assert.Equal("Aplicada", resultado.Resultado);
        await using var final = p.Data.CreateDbContext();
        Assert.Equal(recibio ? EstadoCustodiaBulto.Destinatario : EstadoCustodiaBulto.PendienteDeposito, (await final.Bultos.SingleAsync()).Custodia);
        Assert.Equal(recibio ? EstadoEnvio.EntregaPendienteValidacion : EstadoEnvio.Cancelado, (await final.Envios.SingleAsync()).Estado);
    }
}
