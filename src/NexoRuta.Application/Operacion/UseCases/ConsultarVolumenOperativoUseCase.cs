using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Operacion.Repositories;
using NexoRuta.Application.Operacion.Results;

namespace NexoRuta.Application.Operacion.UseCases;

public sealed class ConsultarVolumenOperativoUseCase(
    IUsuarioActual usuarioActual,
    IHistorialOperativoRepository historial,
    IEstadoProyeccionOperativaRepository estado)
{
    public async Task<ResultadoVolumenOperativo> EjecutarAsync(
        Guid operatorId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        ConsultarHistorialOperativoUseCase.ValidarConsulta(operatorId, from, to);
        var actor = await usuarioActual.ObtenerAsync(ct);
        var scope = ConsultarHistorialOperativoUseCase.ResolverAmbito(actor, operatorId);
        var before = await estado.ReadAsync(operatorId, scope.CommerceId, ct);
        var volumes = await historial.VolumesAsync(operatorId, scope.CommerceId,
            from.ToUniversalTime(), to.ToUniversalTime(), ct);
        var after = await estado.ReadAsync(operatorId, scope.CommerceId, ct);

        return new(ConsultarHistorialOperativoUseCase.Estado(before, after), after.VerificadaEnUtc,
            PoliticaHistorialOperativo.RetencionDias,
            from.ToUniversalTime(), to.ToUniversalTime(), volumes);
    }
}
