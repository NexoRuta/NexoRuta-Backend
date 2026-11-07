using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NexoRuta.Application.Operacion.Repositories;
using NexoRuta.Application.Operacion.UseCases;

namespace NexoRuta.Infrastructure.Operacion;

public static class OperationalHistoryServiceCollectionExtensions
{
    public static IServiceCollection AddOperationalHistory(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["MongoDB:ConnectionString"];
        var databaseName = configuration["MongoDB:Database"];

        if (string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(databaseName))
            services.AddSingleton<IHistorialOperativoRepository, UnavailableOperationalHistoryRepository>();
        else
        {
            if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(databaseName))
                throw new InvalidOperationException("MongoDB:ConnectionString and MongoDB:Database must be configured together.");

            var settings = OperationalMongoClientSettings.Create(connectionString);
            services.AddSingleton<IMongoClient>(_ => new MongoClient(settings));
            services.AddSingleton(provider => provider.GetRequiredService<IMongoClient>().GetDatabase(databaseName));
            services.AddSingleton<OperationalMongoStore>();
            services.AddSingleton<IHistorialOperativoRepository>(provider => provider.GetRequiredService<OperationalMongoStore>());
        }

        services.AddScoped<IEstadoProyeccionOperativaRepository, OperationalProjectionHealthRepository>();
        services.AddScoped<ConsultarHistorialOperativoUseCase>();
        services.AddScoped<ConsultarVolumenOperativoUseCase>();
        return services;
    }
}
