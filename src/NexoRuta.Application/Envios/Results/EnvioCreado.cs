using NexoRuta.Application.Envios.Dtos;
namespace NexoRuta.Application.Envios.Results;

public sealed record EnvioCreado(Guid Id, OrigenEnvio Origen, BultoDetalle Bulto);
