using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Outbox;

namespace NexoRuta.UnitTests.Administracion;

public sealed class PasswordRecoveryOutboxContractTests
{
    [Fact]
    public void RequestEventContainsOnlyVersionedNonSensitiveContractFields()
    {
        var value = new PasswordRecoveryRequestedV1(Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, PasswordRecoveryJson.Options));
        var fields = json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray();

        Assert.Equal(new[] { "eventId", "occurredAt", "schemaVersion", "usuarioId" }, fields);
        Assert.Equal(PasswordRecoveryEventVersion.Current, json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void ProtectedEmailCommandDoesNotPersistRecipientOrTokenAsPlaintext()
    {
        const string recipient = "person@example.test";
        const string token = "reset-token-value";
        var command = new PasswordRecoveryEmailCommand(Guid.CreateVersion7(), recipient, token,
            "https://commerce.example.test/reset", DateTimeOffset.UtcNow.AddMinutes(15));
        var plaintext = JsonSerializer.Serialize(command, PasswordRecoveryJson.Options);
        var protector = new EphemeralDataProtectionProvider().CreateProtector(PasswordRecoveryDataProtection.EmailCommandPurpose);
        var protectedPayload = protector.Protect(plaintext);

        Assert.DoesNotContain(recipient, protectedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(token, protectedPayload, StringComparison.Ordinal);
        Assert.Equal(plaintext, protector.Unprotect(protectedPayload));
    }

    [Fact]
    public void UnifiedOutboxDoesNotInventShipmentOwnershipForGlobalIdentityEvents()
    {
        var userId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var message = OutboxMessage.ForIdentity(userId, OutboxMessageTypes.PasswordRecoveryRequested,
            OutboxDestino.Kafka, $$"""{"eventId":"{{eventId:D}}","schemaVersion":1,"usuarioId":"{{userId:D}}","occurredAt":"2026-10-18T00:00:00Z"}""",
            id: eventId);

        Assert.Null(message.OperadorId);
        Assert.Null(message.EnvioId);
        Assert.Equal(OutboxScope.Identidad, message.Ambito);
        Assert.Equal(userId, message.ClaveAmbito);
        Assert.Equal(OutboxEstado.Pendiente, message.Estado);
        Assert.Equal(OutboxDestino.Kafka, message.Destino);
        Assert.Equal(OutboxPriority.Baja, message.Prioridad);
    }

    [Fact]
    public void RegisteredOutboxContractsChoosePrioritiesWithoutRequestInput()
    {
        var userId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var recoveryEvent = JsonSerializer.Serialize(
            new PasswordRecoveryRequestedV1(eventId, userId, DateTimeOffset.UtcNow), PasswordRecoveryJson.Options);
        Assert.Equal(OutboxPriority.Alta,
            OutboxMessage.ForShipment(Guid.CreateVersion7(), Guid.CreateVersion7(), OutboxMessageTypes.TrackingRequested,
                OutboxDestino.RabbitMq, "{}").Prioridad);
        Assert.Equal(OutboxPriority.Normal,
            OutboxMessage.ForShipment(Guid.CreateVersion7(), Guid.CreateVersion7(), OutboxMessageTypes.ShipmentCreated,
                OutboxDestino.Kafka, "{}").Prioridad);
        Assert.Equal(OutboxPriority.Alta,
            OutboxMessage.ForIdentity(userId, OutboxMessageTypes.PasswordRecoveryEmail, OutboxDestino.RabbitMq,
                "{\"protectedCommand\":\"opaque\"}").Prioridad);
        Assert.Equal(OutboxPriority.Baja,
            OutboxMessage.ForIdentity(userId, OutboxMessageTypes.PasswordRecoveryRequested, OutboxDestino.Kafka,
                recoveryEvent, id: eventId).Prioridad);
        Assert.Throws<ArgumentException>(() => OutboxMessage.ForIdentity(userId, "ArbitraryType", OutboxDestino.Kafka, "{}"));
    }
}
