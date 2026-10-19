using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.IntegrationTests.Fixtures;

internal static class HistoricRecipientEmail
{
    internal static async Task MigrateAsync(NexoRutaDbContext db)
    {
        var migrator = db.GetService<IMigrator>();
        if (!(await db.Database.GetAppliedMigrationsAsync()).Contains("20261019225313_RequireRecipientEmail"))
        {
            await migrator.MigrateAsync("20261019225246_AddRecipientEmail");
            // These fixture recipients explicitly use this address; production migrations never invent contact data.
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Destinatarios\" SET \"Email\"='destinatario@example.test' WHERE \"Email\" IS NULL");
        }
        await migrator.MigrateAsync();
    }
}
