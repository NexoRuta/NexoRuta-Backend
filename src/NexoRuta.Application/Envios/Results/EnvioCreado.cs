using NexoRuta.Application.Envios.Dtos;
namespace NexoRuta.Application.Envios.Results;

public sealed record EnvioCreado(Guid Id, OrigenEnvio Origen, IReadOnlyList<BultoDetalle> Bultos, DateTimeOffset CreadoEnUtc, NexoRuta.Domain.Tarifas.ResultadoCalculoTarifa Tarifa);
