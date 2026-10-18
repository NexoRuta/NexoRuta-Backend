using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Worker.Messaging;

namespace NexoRuta.UnitTests.Messaging;

public sealed class KafkaEnvioCreadoPublisherTests
{
    private static OutboxMessage Event(OutboxDestino destino = OutboxDestino.Kafka, string tipo = OutboxMessageTypes.ShipmentCreated)
    {
        var envioId = Guid.NewGuid();
        var operadorId = Guid.NewGuid();
        return OutboxMessage.ForShipment(operadorId, envioId, tipo, destino, JsonSerializer.Serialize(new
        {
            envioId, operadorId, comercioId = Guid.NewGuid(), estado = "Admitido"
        }));
    }

    [Fact]
    public void Contrato_PreservaPayloadIdentidadYTiempoEnRepublicaciones()
    {
        var evento = Event();
        var primero = KafkaEnvioCreadoPublisher.CrearMensaje(evento);
        var segundo = KafkaEnvioCreadoPublisher.CrearMensaje(evento);
        Assert.Equal("nexoruta.envios.creados.v1", KafkaEnvioCreadoPublisher.Topic);
        Assert.Equal(evento.EnvioId!.Value.ToString("D"), primero.Key);
        Assert.Equal(evento.Payload, primero.Value);
        var esperado = new Dictionary<string, string>
        {
            ["eventId"] = evento.Id.ToString("D"), ["eventType"] = OutboxMessageTypes.ShipmentCreated, ["eventVersion"] = "1",
            ["occurredAt"] = evento.CreadoEnUtc.ToString("O", CultureInfo.InvariantCulture),
            ["operadorId"] = evento.OperadorId!.Value.ToString("D"), ["contentType"] = "application/json"
        };
        Assert.Equal(esperado.Count, primero.Headers.Count);
        foreach (var (nombre, valor) in esperado)
        {
            Assert.Equal(valor, Encoding.UTF8.GetString(primero.Headers.GetLastBytes(nombre)));
            Assert.Equal(primero.Headers.GetLastBytes(nombre), segundo.Headers.GetLastBytes(nombre));
        }
    }

    [Theory]
    [InlineData(OutboxDestino.RabbitMq, "EnvioCreado")]
    [InlineData(OutboxDestino.Kafka, "GenerarSeguimiento")]
    public void Contrato_RechazaOtroDestinoOTipo(OutboxDestino destino, string tipo)
        => Assert.Throws<ArgumentException>(() => KafkaEnvioCreadoPublisher.CrearMensaje(Event(destino, tipo)));

    [Fact]
    public void Configuracion_FuerzaIdempotenciaYAcksAll()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = "example.invalid", ["Kafka:MessageTimeoutMs"] = "5000"
        }).Build();
        var producer = KafkaEnvioCreadoPublisher.CrearConfiguracion(config);
        Assert.Equal("example.invalid", producer.BootstrapServers);
        Assert.True(producer.EnableIdempotence);
        Assert.Equal(Acks.All, producer.Acks);
        Assert.Equal(5000, producer.MessageTimeoutMs);
        Assert.False(producer.AllowAutoCreateTopics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(60001)]
    public void Configuracion_RechazaTimeoutFueraDeReserva(int timeout)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = "example.invalid", ["Kafka:MessageTimeoutMs"] = timeout.ToString()
        }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => KafkaEnvioCreadoPublisher.CrearConfiguracion(config));
    }

    [Fact]
    public async Task CancelacionPrevia_NoConstruyeProductorNiNecesitaBroker()
    {
        using var publisher = new KafkaEnvioCreadoPublisher(new ConfigurationBuilder().Build(),
            NullLogger<KafkaEnvioCreadoPublisher>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.PublicarAsync(OutboxClaim.From(Event()), new CancellationToken(true)));
    }

    [Fact]
    public void Configuracion_ExigeBootstrapServers()
        => Assert.Throws<InvalidOperationException>(() => KafkaEnvioCreadoPublisher.CrearConfiguracion(new ConfigurationBuilder().Build()));
}
