namespace NexoRuta.Application.Envios.Results;

public sealed record EnvioCancelado(Guid CancelacionId, Guid EnvioId, long Version, string Estado);
