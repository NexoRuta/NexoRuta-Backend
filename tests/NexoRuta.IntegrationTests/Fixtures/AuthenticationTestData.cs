using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Fixtures;

internal static class AuthenticationTestData
{
    public const string Password = "Integration-only-password-2026!";
    public const string Issuer = "integration-tests";
    public const string Audience = "integration-client";
    public static readonly string SigningKey = Convert.ToBase64String(
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static Dictionary<string, string?> Settings() => new()
    {
        ["Jwt:Issuer"] = Issuer,
        ["Jwt:Audience"] = Audience,
        ["Jwt:SigningKeyBase64"] = SigningKey,
        ["AccesoInicial:UsuarioPassword"] = Password,
        ["AccesoInicial:OperadorUsuarioPassword"] = Password
    };

    public static string Hash(string email) => new PasswordHasher<string>().HashPassword(email, Password);

    public static Dictionary<string, string?> BootstrapSettings(AccesoInicial initial)
    {
        initial.OperadorId ??= Guid.NewGuid();
        initial.ComercioId ??= Guid.NewGuid();
        initial.OperadorRolCodigo ??= "administrador_operador";
        initial.ComercioRolCodigo ??= "propietario_comercio";
        var settings = Settings();
        settings["AccesoInicial:OperadorId"] = initial.OperadorId.ToString();
        settings["AccesoInicial:ComercioId"] = initial.ComercioId.ToString();
        settings["AccesoInicial:OperadorRolCodigo"] = initial.OperadorRolCodigo;
        settings["AccesoInicial:ComercioRolCodigo"] = initial.ComercioRolCodigo;
        var definitions = new[]
        {
            ("administrador_operador", "Operator administrator", initial.OperadorId, (Guid?)null, new[] { "usuarios.gestionar", "configuracion.gestionar", "envios.consultar", "envios.gestionar" }),
            ("usuario_operador", "Operator user", initial.OperadorId, (Guid?)null, new[] { "envios.consultar", "envios.gestionar" }),
            ("propietario_comercio", "Commerce owner", (Guid?)null, initial.ComercioId, new[] { "usuarios.gestionar", "envios.crear", "envios.consultar" }),
            ("usuario_comercio", "Commerce user", (Guid?)null, initial.ComercioId, new[] { "envios.crear", "envios.consultar" })
        };
        for (var i = 0; i < definitions.Length; i++)
        {
            var (code, name, operatorId, commerceId, permissions) = definitions[i];
            settings[$"Bootstrap:Roles:{i}:Codigo"] = code;
            settings[$"Bootstrap:Roles:{i}:Nombre"] = name;
            if (operatorId is not null) settings[$"Bootstrap:Roles:{i}:OperadorId"] = operatorId.ToString();
            if (commerceId is not null) settings[$"Bootstrap:Roles:{i}:ComercioId"] = commerceId.ToString();
            for (var j = 0; j < permissions.Length; j++) settings[$"Bootstrap:Roles:{i}:Permisos:{j}"] = permissions[j];
        }
        return settings;
    }

    public static DatosInicialesSeeder Seeder(NexoRutaDbContext db, AccesoInicial initial) => new(
        db, initial, new ConfigurationBuilder().AddInMemoryCollection(BootstrapSettings(initial)).Build(),
        new PasswordHasher<string>());
}
