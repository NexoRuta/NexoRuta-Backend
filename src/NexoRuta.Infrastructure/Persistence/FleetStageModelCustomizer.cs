using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NexoRuta.Domain.Reparto;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
namespace NexoRuta.Infrastructure.Persistence;

// Explicit maintenance projection, never the normal runtime model or a historical source reconstruction.
// Only B2 fleet commands are admitted by FleetStageMaintenance; B3/B4/B5 endpoints remain compiled but blocked.
public sealed class FleetStageModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.Entity<HojaRuta>().Ignore(x => x.PlanTemporal);
        modelBuilder.Entity<ParadaRuta>().Ignore(x => x.ServicioMinutos)
            .Ignore(x => x.ResultadoVisita).Ignore(x => x.BultoIds);
        // Stage is an explicitly supported maintenance schema. Do not query columns
        // introduced by partial delivery; normal writers remain blocked by StageOnly.
        modelBuilder.Entity<Bulto>().Ignore(x => x.Resultado).Ignore(x => x.MotivoNoEntrega)
            .Ignore(x => x.EntregadoEnUtc).Ignore(x => x.PruebaVenceEnUtc);
        modelBuilder.Entity<Envio>().Ignore(x => x.EsRetiro).Ignore(x => x.MotivoRetiro)
            .Ignore(x => x.FranjaRetiroDesdeUtc).Ignore(x => x.FranjaRetiroHastaUtc)
            .Ignore(x => x.SolicitudRetiroId).Ignore(x => x.SolicitudRetiroHuella);
        modelBuilder.Entity<VersionReglasOperativas>().Ignore(x => x.PermiteEntregaParcial)
            .Ignore(x => x.PoliticaPendientesParcial).Ignore(x => x.RecargoNuevaVisitaUyu);
    }
}
