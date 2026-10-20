namespace NexoRuta.Application.Seguimiento.Interfaces;

public interface ICancelacionPublica
{
    Task<string> SolicitarCodigoAsync(string token, CancellationToken ct);
    Task<string> ConfirmarAsync(string token, string codigo, CancellationToken ct);
}
