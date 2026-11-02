using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NexoRuta.Domain.Reparto;
namespace NexoRuta.Infrastructure.Persistence;

// Explicit maintenance projection, never the normal runtime model or a historical source reconstruction.
// Only B2 fleet commands are admitted by FleetStageMaintenance; B3/B4/B5 endpoints remain compiled but blocked.
public sealed class FleetStageModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.Entity<HojaRuta>().Ignore(x => x.PlanTemporal);
        modelBuilder.Entity<ParadaRuta>().Ignore(x => x.ServicioMinutos);
    }
}
