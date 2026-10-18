using NexoRuta.Application.Administracion.Results;

namespace NexoRuta.Application.Administracion.Interfaces;

public interface IRecuperacionPassword
{
    Task SolicitarAsync(string email, CancellationToken ct);
    Task<ResultadoRestablecimientoPassword> RestablecerAsync(Guid usuarioId, string token, string nuevaPassword, CancellationToken ct);
}
