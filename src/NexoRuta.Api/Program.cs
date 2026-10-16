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
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddNexoRutaPersistence(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUsuarioActual, UsuarioActualHttp>();


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



if (builder.Configuration.GetValue<bool>("OpenTelemetry:Enabled"))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("NexoRuta.Api"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter());
}


builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode =
        StatusCodes.Status429TooManyRequests;

    options.AddPolicy("seguimiento-publico", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey:
                context.Connection.RemoteIpAddress?.ToString()
                ?? "sin-ip",

            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));


    options.AddPolicy("auth-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey:
                context.Connection.RemoteIpAddress?.ToString()
                ?? "sin-ip",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));

});



var app = builder.Build();

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
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();

public partial class Program { }
