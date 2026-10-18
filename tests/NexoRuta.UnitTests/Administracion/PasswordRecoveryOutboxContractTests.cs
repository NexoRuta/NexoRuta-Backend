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
    public void SecurityOutboxDoesNotInventShipmentOwnershipForGlobalIdentityEvents()
    {
        var message = new SecurityOutboxMessage(
            SecurityOutboxMessageTypes.PasswordRecoveryRequested,
            SecurityOutboxDestination.Kafka,
            "{}", id: Guid.CreateVersion7());

        Assert.DoesNotContain(typeof(SecurityOutboxMessage).GetProperties(), property => property.Name is "OperadorId" or "EnvioId");
        Assert.Equal(SecurityOutboxState.Pending, message.State);
        Assert.Equal(SecurityOutboxDestination.Kafka, message.Destination);
    }
}
