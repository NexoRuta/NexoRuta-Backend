namespace NexoRuta.IntegrationTests.Operacion;

// Docker VFS makes full image copies. Run these complete multi-service scenarios
// exclusively to avoid exhausting ephemeral disk during parallel container startup.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OperationalInfrastructureCollection
{
    public const string Name = "Operational infrastructure";
}
