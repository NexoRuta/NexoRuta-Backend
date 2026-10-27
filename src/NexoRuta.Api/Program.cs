using NexoRuta.Infrastructure.Hosting;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Core.Health;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.ZonasCobertura;
var platformCommand = args.FirstOrDefault() == "platform-admin";
if (platformCommand && !args.SequenceEqual(["platform-admin", "create"]))
{
    Console.Error.WriteLine("Usage: platform-admin create (interactive credentials; no additional arguments).");
    Environment.ExitCode = 2;
    return;
}
var localBootstrapCommand = args.SequenceEqual(["local-bootstrap"]);
var migrationCommand = args.SequenceEqual(["database-migrate"]);
var catalogImportCommand = args.Length > 0 && args[0] == DepartamentoCatalogImporter.ImportCommandName;
var catalogImportUpdate = args.Length == 2 && args[1] == DepartamentoCatalogImporter.AllowUpdateOption;
if (catalogImportCommand && (args.Length > 2 || (args.Length == 2 && !catalogImportUpdate)))
{
    Console.Error.WriteLine($"Usage: {DepartamentoCatalogImporter.ImportCommandName} [{DepartamentoCatalogImporter.AllowUpdateOption}]");
    Environment.ExitCode = 2;
    return;
}
var commandMode = platformCommand || localBootstrapCommand || migrationCommand || catalogImportCommand;
var builder = WebApplication.CreateBuilder(commandMode ? [] : args);
if (localBootstrapCommand)
{
    builder.Configuration["DatabaseInitialization:SeedData"] = "true";
    builder.Configuration["PlatformInitialization:Enabled"] = "true";
}

builder.Services.AddControllers();
builder.Services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, _) =>
{
    var type = context.JsonTypeInfo.Type;
    if (type == typeof(decimal) || Nullable.GetUnderlyingType(type) == typeof(decimal))
        schema.Format = "decimal";
    return Task.CompletedTask;
}));
builder.Services.AddNexoRutaPersistence(builder.Configuration);
var dataProtection = builder.Services.AddDataProtection().SetApplicationName(PasswordRecoveryDataProtection.ApplicationName);
var dataProtectionKeys = builder.Configuration[PasswordRecoveryDataProtection.KeysDirectoryConfigurationKey];
var sharedDataProtection = builder.Services.AddSharedDataProtection(
    builder.Configuration, PasswordRecoveryDataProtection.ApplicationName, "api");
if (!sharedDataProtection)
{
    if (!string.IsNullOrWhiteSpace(dataProtectionKeys))
        dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeys));
    else if (builder.Environment.IsProduction()
        && !platformCommand && !localBootstrapCommand && !migrationCommand && !catalogImportCommand)
        throw new InvalidOperationException("Production requires persistent DataProtection:KeysDirectory.");
}
builder.Services.AddOptions<PasswordRecoveryOptions>()
    .Bind(builder.Configuration.GetSection("PasswordRecovery"))
    .Validate(options => options.TokenLifespanSeconds is > 0 and <= 3600
        && Uri.TryCreate(options.ResetPageUrl, UriKind.Absolute, out var url)
        && url.Scheme is "https" or "http"
        && (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing") || url.Scheme == "https")
        && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment),
        "Password recovery requires an absolute trusted HTTPS reset page URL (HTTP only in Development) and a token lifespan under one hour.")
    .ValidateOnStart();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUsuarioActual, UsuarioActualHttp>();
if (platformCommand)
{
    await using var commandApp = builder.Build();
    Environment.ExitCode = await NexoRuta.Api.Features.Plataforma.InicializarPlataformaCommand.EjecutarAsync(commandApp.Services);
    return;
}
if (localBootstrapCommand)
{
    await using var commandApp = builder.Build();
    await using var scope = commandApp.Services.CreateAsyncScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DatosInicialesSeeder>();
    await seeder.SeedPlataformaAsync();
    await seeder.SeedAsync();
    return;
}
if (migrationCommand)
{
    await using var commandApp = builder.Build();
    await using var scope = commandApp.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>().Database.MigrateAsync();
    return;
}
if (catalogImportCommand)
{
    await using var commandApp = builder.Build();
    await using var scope = commandApp.Services.CreateAsyncScope();
    var result = await scope.ServiceProvider.GetRequiredService<DepartamentoCatalogImporter>()
        .ImportarCatalogoIncluidoAsync(catalogImportUpdate);
    Console.WriteLine($"Department catalog: {result.DepartmentCount} rows, {result.VersionFuente}, alreadyImported={result.AlreadyImported}.");
    return;
}


var jwt = new JwtConfiguracion(builder.Configuration);

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<JwtValidacionEventos>();
builder.Services.AddScoped<IAuthorizationHandler,PermisoAuthorizationHandler>();
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,

            ValidateAudience = true,
            ValidAudience = jwt.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = jwt.SigningKey,

            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        options.EventsType = typeof(JwtValidacionEventos);
    });


builder.Services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgres");



builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !context.User.HasClaim(
            AccesoSeleccionado.ClaimAmbito, AccesoSeleccionado.ClaimSesionSuspendida))
        .Build();
    options.AddPolicy("organizacion-suspendida", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => context.Resource is HttpContext http &&
            http.Items[typeof(ContextoOrganizacionSuspendida)] is ContextoOrganizacionSuspendida));
    options.AddPolicy("organizacion", policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => context.Resource is HttpContext http && http.Items[typeof(ContextoUsuario)] is ContextoUsuario));
    options.AddPolicy(RolPlataforma.GestionarOrganizaciones, policy => policy.RequireAuthenticatedUser()
        .RequireAssertion(context => context.Resource is HttpContext http &&
            http.Items[typeof(ContextoPlataforma)] is ContextoPlataforma { PuedeGestionarOrganizaciones: true }));
    // Política existente para cuentas de comercio.
    options.AddPolicy(nameof(TipoAccesoUsuario.Comercio), policy =>
        policy.RequireAuthenticatedUser()
            .RequireClaim(
                AccesoSeleccionado.ClaimTipoAcceso,
                nameof(TipoAccesoUsuario.Comercio)));

    // Política demo existente.
    options.AddPolicy("seguimiento-demo", policy =>
        policy.RequireAuthenticatedUser()
            .RequireAssertion(
                _ => builder.Environment.IsDevelopment()));

    // Políticas RBAC.
    foreach (var permiso in CatalogoPermisos.Todos)
    {
        options.AddPolicy(permiso.Codigo, policy =>
            policy.RequireAuthenticatedUser()
                .AddRequirements(
                    new PermisoRequirement(permiso.Codigo)));
    }
});



builder.Services.AddTrustedProxyHeaders(builder.Configuration);
builder.Services.AddHostingTelemetry(builder.Configuration, "NexoRuta.Api");
var rateConnection = builder.Configuration.GetConnectionString("RateLimits");
NpgsqlDataSource? rateDataSource = string.IsNullOrWhiteSpace(rateConnection) ? null : NpgsqlDataSource.Create(rateConnection);
if (rateDataSource is not null) builder.Services.AddSingleton(rateDataSource);
RateLimitPartition<string> RequestPartition(string policy, string key, int limit)
    => rateDataSource is null
        ? RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })
        : RateLimitPartition.Get(key, _ => new PostgresRateLimiter(rateDataSource, policy, key, limit));


builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(PostgresRateLimiter.BackendUnavailableMetadataName, out var unavailable) && unavailable is true)
            context.HttpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        else if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Task.CompletedTask;
    };
    options.AddPolicy("geocodificacion", context => RequestPartition("geocodificacion",
        context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip", 10));
    options.AddPolicy("seguimiento-publico", context => RequestPartition("seguimiento-publico",
        context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip", 20));
    foreach (var policy in new[] { "auth-login", "auth-register", "auth-refresh", "auth-password-recovery" })
        options.AddPolicy(policy, context => RequestPartition(policy,
            context.Connection.RemoteIpAddress?.ToString() ?? "sin-ip", policy == "auth-refresh" ? 30 : 5));
});


var app = builder.Build();
app.UseForwardedHeaders();
{
    await using var scope = app.Services.CreateAsyncScope();
    if (app.Configuration.GetValue<bool>("DatabaseInitialization:ApplyMigrations"))
        await scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>().Database.MigrateAsync();
    if (app.Configuration.GetValue<bool>("DatabaseInitialization:SeedData"))
        await scope.ServiceProvider.GetRequiredService<DatosInicialesSeeder>().SeedAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseMiddleware<RlsTransactionMiddleware>();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();

public partial class Program { }
