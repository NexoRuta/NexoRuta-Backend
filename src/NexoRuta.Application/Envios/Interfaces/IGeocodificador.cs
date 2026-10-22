using NexoRuta.Application.Envios.Dtos;

namespace NexoRuta.Application.Envios.Interfaces;

public interface IGeocodificador
{
    string Atribucion { get; }
    Task<IReadOnlyList<PropuestaDestinoDatos>> BuscarAsync(string direccion, CancellationToken ct);
}
