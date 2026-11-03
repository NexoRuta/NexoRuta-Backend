using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Exceptions;
namespace NexoRuta.Application.Reparto.UseCases;
public sealed partial class GestionarRutasUseCase
{
    public async Task<ConsultaRecepcionDatos?> ConsultarRecepcionAsync(string codigo, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.OperadorId is null || actor.ComercioId is not null) throw new RepartoNoPermitidoException();
        return await rutas.ConsultarRecepcionAsync(actor.OperadorId.Value, codigo, ct);
    }
    public async Task<ResultadoRecepcionDatos?> CotejarBultoAsync(Guid id, CotejoBultoDatos datos, CancellationToken ct)
    {
        var actor = await usuario.ObtenerAsync(ct);
        if (actor.OperadorId is null || actor.ComercioId is not null) throw new RepartoNoPermitidoException();
        return await rutas.CotejarBultoAsync(actor,id,datos,ct);
    }
}
