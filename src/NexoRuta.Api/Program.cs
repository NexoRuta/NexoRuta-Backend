using Microsoft.EntityFrameworkCore;
using NexoRuta.Api;
using NexoRuta.Infrastructure.Persistence;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddNexoRutaPersistence(builder.Configuration);
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

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
    await db.Database.MigrateAsync();
    await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();

    app.MapOpenApi();
    app.MapControllers();
}

app.UseAuthorization();
app.MapHealthChecks("/health/ready");
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));

app.Run();

public partial class Program { }
