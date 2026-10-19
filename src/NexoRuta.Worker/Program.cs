using System.Net.Mail;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Infrastructure.Correo;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.Worker.Messaging;
using NexoRuta.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var transport = builder.Configuration[WorkerConfigurationKeys.Transport] ?? WorkerTransportNames.RabbitMq;
if (transport is not (WorkerTransportNames.RabbitMq or WorkerTransportNames.Kafka))
    throw new InvalidOperationException("Messaging:Transport must be RabbitMq or Kafka.");

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required for this Worker.");
builder.Services.AddDbContext<NexoRutaDbContext>(options => options.UseNexoRutaPostgres(connectionString));
builder.Services.AddScoped<EfOutboxRepository>();

if (transport == WorkerTransportNames.RabbitMq)
{
    builder.Services.AddSingleton<RabbitMqOutboxPublisher>();
    builder.Services.AddSingleton<IOutboxMessageHandler, RabbitTrackingOutboxHandler>();
    builder.Services.AddSingleton<IOutboxMessageHandler, RabbitPasswordRecoveryEmailOutboxHandler>();
    builder.Services.AddSingleton<OutboxHandlerRegistry>();
    builder.Services.AddHostedService<Worker>();
    builder.Services.AddScoped<IConsultaEstadoEnvio, EfConsultaEstadoEnvio>();
    builder.Services.AddScoped<IEnlacesSeguimientoRepository, EfEnlacesSeguimientoRepository>();
    builder.Services.AddScoped<CrearEnlaceSeguimientoUseCase>();
    builder.Services.AddSingleton<CifradorTokenSeguimiento>();
    builder.Services.AddScoped<ProcesarGeneracionSeguimiento>();
    builder.Services.AddHostedService<GenerarSeguimientoConsumer>();

    var smtpSection = builder.Configuration.GetSection(ConfiguracionSmtp.ConfigurationSection);
    builder.Services.AddOptions<ConfiguracionSmtp>().Bind(smtpSection)
        .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port is > 0 and <= 65535
            && MailAddress.TryCreate(options.FromAddress, out _)
            && (string.IsNullOrWhiteSpace(options.Username) == string.IsNullOrWhiteSpace(options.Password)),
            "Email:Smtp requires a host, valid port and from address; SMTP credentials must be provided together.")
        .ValidateOnStart();
    builder.Services.AddSingleton<ICorreoSender, SmtpCorreoSender>();
    var keysDirectory = builder.Configuration[PasswordRecoveryDataProtection.KeysDirectoryConfigurationKey]
        ?? throw new InvalidOperationException("Data protection key directory is required by the RabbitWorker.");
    builder.Services.AddDataProtection()
        .SetApplicationName(PasswordRecoveryDataProtection.ApplicationName)
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    builder.Services.AddHostedService<PasswordRecoveryEmailConsumer>();
    builder.Services.AddHostedService<PasswordRecoveryEmailDeadLetterConsumer>();
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
    builder.Services.AddSingleton<KafkaEnvioCreadoPublisher>();
    builder.Services.AddSingleton<IOutboxMessageHandler, KafkaShipmentCreatedOutboxHandler>();
    builder.Services.AddSingleton<IOutboxMessageHandler, KafkaPasswordRecoveryRequestedOutboxHandler>();
    builder.Services.AddSingleton<OutboxHandlerRegistry>();
    builder.Services.AddHostedService<KafkaOutboxWorker>();
    var mongoConnection = builder.Configuration[WorkerConfigurationKeys.MongoConnectionString]
        ?? throw new InvalidOperationException("MongoDB connection string is required.");
    var mongoDatabase = builder.Configuration[WorkerConfigurationKeys.MongoDatabase]
        ?? throw new InvalidOperationException("MongoDB database name is required.");
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnection));
    builder.Services.AddSingleton(provider => provider.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabase));
    builder.Services.AddSingleton<PasswordRecoveryMongoStore>();
    builder.Services.AddHostedService<PasswordRecoveryMongoConsumer>();
}

await builder.Build().RunAsync();
