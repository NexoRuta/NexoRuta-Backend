using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NexoRuta.Infrastructure.Hosting;

public sealed record SharedKeyRing(string Schema);

public sealed class SharedKeyDbContext(DbContextOptions<SharedKeyDbContext> options, SharedKeyRing ring)
    : DbContext(options), IDataProtectionKeyContext
{
    public string KeySchema => ring.Schema;
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<DataProtectionKey>().ToTable("DataProtectionKeys", KeySchema);
}

public sealed class SharedKeyModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (context.GetType(), ((SharedKeyDbContext)context).KeySchema, designTime);
}

public static class SharedDataProtection
{
    public static bool AddSharedDataProtection(this IServiceCollection services, IConfiguration configuration,
        string applicationName, string ring)
    {
        var connection = configuration.GetConnectionString("DataProtection");
        if (string.IsNullOrWhiteSpace(connection)) return false;
        if (ring is not ("api" or "admin" or "backoffice" or "commerce" or "tracking"))
            throw new InvalidOperationException("Unknown Data Protection key ring.");
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        var certificate = LoadCertificate(configuration["DataProtection:CertificatePath"],
            configuration["DataProtection:CertificatePassword"]);
        var certificates = new List<X509Certificate2> { certificate };
        foreach (var previous in configuration.GetSection("DataProtection:PreviousCertificates").GetChildren())
            certificates.Add(LoadCertificate(previous["Path"], previous["Password"]));

        services.AddSingleton(new SharedKeyRing("nexoruta_keys_" + ring));
        services.AddDbContext<SharedKeyDbContext>(options => options.UseNpgsql(connection)
            .ReplaceService<IModelCacheKeyFactory, SharedKeyModelCacheKeyFactory>());
        services.AddDataProtection().SetApplicationName(applicationName)
            .PersistKeysToDbContext<SharedKeyDbContext>()
            .ProtectKeysWithCertificate(certificate)
            .UnprotectKeysWithAnyCertificate(certificates.ToArray());
        return true;
    }

    private static X509Certificate2 LoadCertificate(string? path, string? password)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Shared Data Protection requires a certificate file.");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password,
            X509KeyStorageFlags.EphemeralKeySet);
        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new InvalidOperationException("The Data Protection certificate requires its private key.");
        }
        return certificate;
    }
}
