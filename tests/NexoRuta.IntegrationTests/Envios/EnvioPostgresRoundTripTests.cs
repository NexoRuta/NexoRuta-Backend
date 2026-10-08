using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Envios;
using NexoRuta.Infrastructure.Persistence;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class EnvioPostgresRoundTripTests
{
    [Fact]
    public async Task AltaDeEnvioYBulto_SePersistenYSeConsultanConElMismoComercio()
    {
        var connectionString = Environment.GetEnvironmentVariable("NEXORUTA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = "Host=127.0.0.1;Port=5432;Database=nexoruta;Username=nexoruta;Password=nexoruta_dev";
        var options = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = new NexoRutaDbContext(options);
        await db.Database.MigrateAsync();
        await new DemoDataSeeder(db).SeedAsync();
        var repository = new EfEnviosRepository(db);
        var useCase = new CrearEnvioUseCase(repository);
        var codigo = $"MONITOREO-{Guid.CreateVersion7():N}";

        var created = await useCase.EjecutarAsync(new CrearEnvioCommand(
            "Destinatario de prueba", "Av. Demo 123", codigo, 1750m, 32m, 21m, 11m));
        var listed = await new ListarEnviosUseCase(repository).EjecutarAsync();
        var persisted = Assert.Single(listed, shipment => shipment.Id == created.Id);

        Assert.Equal("demo@nexoruta.local", persisted.UsuarioEmail);
        Assert.Equal("Operador Demo", persisted.OperadorNombre);
        Assert.Equal("Comercio Demo", persisted.ComercioNombre);
        Assert.Equal("Destinatario de prueba", persisted.DestinatarioNombre);
        Assert.Equal("Av. Demo 123", persisted.Direccion);
        var package = Assert.Single(persisted.Bultos);
        Assert.Equal(codigo, package.Codigo);
        Assert.Equal(1750m, package.PesoGramos);
        Assert.Equal(32m, package.LargoCentimetros);
        Assert.Equal(21m, package.AnchoCentimetros);
        Assert.Equal(11m, package.AltoCentimetros);
    }
}
