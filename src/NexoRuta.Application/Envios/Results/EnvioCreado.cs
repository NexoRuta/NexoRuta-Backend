namespace NexoRuta.Application.Envios;

public sealed record EnvioCreado(Guid Id, OrigenEnvio Origen, BultoDetalle Bulto);
