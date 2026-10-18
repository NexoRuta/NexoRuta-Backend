using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using MongoDB.Driver;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Worker;
using NexoRuta.Application.Seguimiento.Interfaces;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Seguimiento.Consultas;
using NexoRuta.Infrastructure.Seguimiento.Repositories;
using NexoRuta.Infrastructure.Seguimiento.Processing;
using NexoRuta.Infrastructure.Seguimiento.Security;
using NexoRuta.Infrastructure.Administracion.Autenticacion;
using NexoRuta.Worker.Messaging;
using NexoRuta.Infrastructure.Correo;
using NexoRuta.Application.Administracion.Interfaces;
using System.Net.Mail;
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// Separate host processes: the RabbitMQ consumer can intentionally stop its host.
// Kafka must not share that failure/shutdown boundary.
var transport = builder.Configuration[WorkerConfigurationKeys.Transport] ?? WorkerTransportNames.RabbitMq;
if (transport is WorkerTransportNames.RabbitMq or WorkerTransportNames.Kafka)
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required for this Worker.");
    builder.Services.AddDbContext<NexoRutaDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddScoped<EfOutboxRepository>();
    builder.Services.AddScoped<SecurityOutboxRepository>();
}

if (transport == WorkerTransportNames.Kafka)
{
    builder.Services.AddSingleton<KafkaEnvioCreadoPublisher>();
    builder.Services.AddHostedService<KafkaOutboxWorker>();
    builder.Services.AddSingleton<KafkaPasswordRecoveryPublisher>();
    builder.Services.AddSingleton<ISecurityOutboxTransport>(services => services.GetRequiredService<KafkaPasswordRecoveryPublisher>());
    builder.Services.AddHostedService<SecurityOutboxPublisherWorker>();
}
else if (transport == WorkerTransportNames.RabbitMq)
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
    builder.Services.AddSingleton<RabbitMqPasswordRecoveryPublisher>();
    builder.Services.AddSingleton<ISecurityOutboxTransport>(services => services.GetRequiredService<RabbitMqPasswordRecoveryPublisher>());
    builder.Services.AddHostedService<SecurityOutboxPublisherWorker>();
}
else if (transport == WorkerTransportNames.RabbitMqPasswordRecoveryMail)
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required for the mail Worker.");
    builder.Services.AddDbContext<NexoRutaDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddScoped<SecurityOutboxRepository>();
    var smtpSection = builder.Configuration.GetSection(ConfiguracionSmtp.ConfigurationSection);
    builder.Services.AddOptions<ConfiguracionSmtp>().Bind(smtpSection)
        .Validate(options => !string.IsNullOrWhiteSpace(options.Host) && options.Port is > 0 and <= 65535
            && MailAddress.TryCreate(options.FromAddress, out _)
            && (string.IsNullOrWhiteSpace(options.Username) == string.IsNullOrWhiteSpace(options.Password)),
            "Email:Smtp requires a host, valid port and from address; SMTP credentials must be provided together.")
        .ValidateOnStart();
    builder.Services.AddSingleton<ICorreoSender, SmtpCorreoSender>();
    var keysDirectory = builder.Configuration[PasswordRecoveryDataProtection.KeysDirectoryConfigurationKey]
        ?? throw new InvalidOperationException("Data protection key directory is required by the email worker.");
    builder.Services.AddDataProtection()
        .SetApplicationName(PasswordRecoveryDataProtection.ApplicationName)
        .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    builder.Services.AddHostedService<PasswordRecoveryEmailConsumer>();
}
else if (transport == WorkerTransportNames.KafkaPasswordRecoveryMongo)
{
    var mongoConnection = builder.Configuration[WorkerConfigurationKeys.MongoConnectionString]
        ?? throw new InvalidOperationException("MongoDB connection string is required.");
    var mongoDatabase = builder.Configuration[WorkerConfigurationKeys.MongoDatabase]
        ?? throw new InvalidOperationException("MongoDB database name is required.");
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnection));
    builder.Services.AddSingleton(provider => provider.GetRequiredService<IMongoClient>().GetDatabase(mongoDatabase));
    builder.Services.AddSingleton<PasswordRecoveryMongoStore>();
    builder.Services.AddHostedService<PasswordRecoveryMongoConsumer>();
}
else
{
    throw new InvalidOperationException("Messaging:Transport must select a registered Worker transport.");
}

var host = builder.Build();
await host.RunAsync();
