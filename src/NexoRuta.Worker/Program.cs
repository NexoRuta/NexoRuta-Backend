using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Worker;
using NexoRuta.Worker.Messaging;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Security;
var builder = Host.CreateApplicationBuilder(args);

var connectionString =
    builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Falta ConnectionStrings:Postgres para el Worker.");

builder.Services.AddDbContext<NexoRutaDbContext>(
    options => options.UseNpgsql(connectionString));

builder.Services.AddScoped<EfOutboxRepository>();

builder.Services.AddSingleton<RabbitMqSeguimientoPublisher>();

builder.Services.AddHostedService<Worker>();


builder.Logging.AddFilter(
    "Microsoft.EntityFrameworkCore.Database.Command",
    LogLevel.Warning);

builder.Services.AddScoped<
    IConsultaEstadoEnvio,
    EfConsultaEstadoEnvio>();

builder.Services.AddScoped<
    IEnlacesSeguimientoRepository,
    EfEnlacesSeguimientoRepository>();

builder.Services.AddScoped<CrearEnlaceSeguimientoUseCase>();

builder.Services.AddSingleton<CifradorTokenSeguimiento>();

builder.Services.AddScoped<ProcesarGeneracionSeguimiento>();

// Publicación desde Outbox y consumo de RabbitMQ.
builder.Services.AddHostedService<GenerarSeguimientoConsumer>();

var host = builder.Build();
await host.RunAsync();