using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
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

    public static async Task<int> ImportAsync(IServiceProvider services, string directory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The legacy Data Protection key directory is missing.");

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SharedKeyDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize imports across both hosts without affecting ordinary key reads or creation.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({db.KeySchema}, 0))", cancellationToken);
        var existing = (await db.DataProtectionKeys.AsNoTracking().Select(key => key.Xml)
            .ToListAsync(cancellationToken)).Select(ParseXml).ToList();
        var imported = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var xml = ParseXml(await File.ReadAllTextAsync(file, cancellationToken));
            if (xml.Name.NamespaceName.Length != 0 || xml.Name.LocalName is not ("key" or "revocation"))
                throw new InvalidDataException("A legacy key file contains an unsupported XML element.");
            if (existing.Any(key => XNode.DeepEquals(key, xml))) continue;
            db.DataProtectionKeys.Add(new DataProtectionKey
            {
                FriendlyName = Path.GetFileNameWithoutExtension(file),
                Xml = xml.ToString(SaveOptions.DisableFormatting)
            });
            existing.Add(xml);
            imported++;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return imported;
    }

    private static XElement ParseXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new InvalidDataException("An empty Data Protection key was found.");
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 4 * 1024 * 1024
        });
        return XElement.Load(reader);
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
