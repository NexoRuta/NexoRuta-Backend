using MongoDB.Driver;

namespace NexoRuta.Infrastructure.Operacion;

public static class OperationalMongoClientSettings
{
    public static MongoClientSettings Create(string connectionString)
    {
        var settings = MongoClientSettings.FromUrl(MongoUrl.Create(connectionString));
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        settings.SocketTimeout = TimeSpan.FromSeconds(5);
        return settings;
    }
}
