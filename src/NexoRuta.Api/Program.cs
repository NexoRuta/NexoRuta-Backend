using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api;
using NexoRuta.Api.Seguridad;
using NexoRuta.Application.Administracion;
using NexoRuta.Domain.Administracion;
using NexoRuta.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddNexoRutaPersistence(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IUsuarioActual, UsuarioActualHttp>();

builder.Services.AddAuthentication(AccesoSeleccionado.Esquema)
    .AddScheme<AuthenticationSchemeOptions, AccesoSeleccionadoHandler>(AccesoSeleccionado.Esquema, _ => { });

builder.Services.AddAuthorization(options => options.AddPolicy(nameof(TipoAccesoUsuario.Comercio),
    policy => policy.RequireAuthenticatedUser().RequireClaim(
        AccesoSeleccionado.ClaimTipoAcceso, nameof(TipoAccesoUsuario.Comercio))));

builder.Services.AddHealthChecks().AddCheck<PostgreSqlHealthCheck>("postgres");

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

var app = builder.Build();

{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DatosInicialesSeeder>().SeedAsync();
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();

public partial class Program { }
