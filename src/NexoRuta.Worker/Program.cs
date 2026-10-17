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

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// Separate host processes: the RabbitMQ consumer can intentionally stop its host.
// Kafka must not share that failure/shutdown boundary.
var transport = builder.Configuration["Messaging:Transport"] ?? "RabbitMq";
if (transport == "Kafka")
{
    builder.Services.AddSingleton<KafkaEnvioCreadoPublisher>();
    builder.Services.AddHostedService<KafkaOutboxWorker>();
}
else if (transport == "RabbitMq")
{
    builder.Services.AddSingleton<RabbitMqSeguimientoPublisher>();

    builder.Services.AddHostedService<Worker>();

    builder.Services.AddScoped<IConsultaEstadoEnvio, EfConsultaEstadoEnvio>();

    builder.Services.AddScoped<IEnlacesSeguimientoRepository, EfEnlacesSeguimientoRepository>();

    builder.Services.AddScoped<CrearEnlaceSeguimientoUseCase>();

    builder.Services.AddSingleton<CifradorTokenSeguimiento>();

    builder.Services.AddScoped<ProcesarGeneracionSeguimiento>();

    // Publicación desde Outbox y consumo de RabbitMQ.
    builder.Services.AddHostedService<GenerarSeguimientoConsumer>();
}
else if (transport == "Push")
{
    builder.Services.AddScoped<NexoRuta.Infrastructure.Notificaciones.CifradorRegistroPush>();
    builder.Services.AddScoped<NexoRuta.Infrastructure.Notificaciones.EfDespachoPushRepository>();
    builder.Services.AddSingleton<ITransporteFcm, TransporteFcm>();
    builder.Services.AddHostedService<PushOutboxWorker>();
}
else
{
    throw new InvalidOperationException("Messaging:Transport must be RabbitMq, Kafka or Push.");
}

var host = builder.Build();
await host.RunAsync();
