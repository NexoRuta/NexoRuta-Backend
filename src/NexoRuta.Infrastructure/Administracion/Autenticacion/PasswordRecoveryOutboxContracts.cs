namespace NexoRuta.Infrastructure.Administracion.Autenticacion;

using System.Text.Json;

public static class PasswordRecoveryDataProtection
{
    public const string ApplicationName = "NexoRuta.Api";
    public const string EmailCommandPurpose = "NexoRuta.PasswordRecovery.EmailCommand.v1";
    public const string KeysDirectoryConfigurationKey = "DataProtection:KeysDirectory";
}

public static class PasswordRecoveryEventVersion
{
    public const int Current = 1;
}

public static class PasswordRecoveryJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

public sealed record PasswordRecoveryEmailCommand(
    Guid MessageId,
    string Recipient,
    string Token,
    string Link,
    DateTimeOffset ExpiresAtUtc);

public sealed record PasswordRecoveryRequestedV1(
    Guid EventId,
    Guid UsuarioId,
    DateTimeOffset OccurredAt,
    int SchemaVersion = PasswordRecoveryEventVersion.Current);
