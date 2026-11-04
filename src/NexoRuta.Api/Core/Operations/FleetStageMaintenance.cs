using Microsoft.EntityFrameworkCore;
using NexoRuta.Infrastructure.Persistence;
namespace NexoRuta.Api.Core.Operations;

public static class FleetStageMaintenance
{
    public const string ConfigurationKey="FleetTransition:StageOnly";
    public const string Stage="20261101184716_StageFleetOccupations";
    public static void ValidateConfiguration(IConfiguration configuration, bool commandMode)
    {
        if(commandMode || configuration.GetValue<bool>("DatabaseInitialization:ApplyMigrations")
            || configuration.GetValue<bool>("DatabaseInitialization:SeedData")
            || configuration.GetValue<bool>("PlatformInitialization:Enabled")
            || configuration.GetValue<bool>("RoutingGeoapify:PermitirLlamadasReales"))
            throw new InvalidOperationException("Stage maintenance forbids migration/bootstrap commands, automatic migrations/seeds and real routing calls.");
    }
    public static async Task VerifySchemaAsync(NexoRutaDbContext db, CancellationToken ct=default)
    {
        var last=(await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
        if(last!=Stage)throw new InvalidOperationException("Stage maintenance requires exactly StageFleetOccupations, without Enforce, B3 or B4. No migration was applied.");
    }
    public static bool Allows(HttpRequest r)
    {
        var path=r.Path.Value??"";
        if(HttpMethods.IsGet(r.Method))return path is "/health/ready" or "/health/live" or "/api/reparto/flota" or "/api/usuarios/actual" or "/api/usuarios/actual/permisos";
        if(!HttpMethods.IsPost(r.Method))return false;
        if(path=="/api/auth/login")return true;
        var pieces=path.Split('/',StringSplitOptions.RemoveEmptyEntries);
        return pieces.Length==5&&pieces[0]=="api"&&pieces[1]=="reparto"&&pieces[2]=="flota"&&Guid.TryParseExact(pieces[3],"D",out _)
            &&pieces[4] is "conciliar" or "anular" or "retorno" or "liberar" or "verificar-inicial" or "recibir-carga-inicial";
    }
}
