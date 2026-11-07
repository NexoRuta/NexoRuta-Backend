using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Operacion.Exceptions;
using NexoRuta.Application.Operacion.Repositories;
using NexoRuta.Application.Operacion.Results;

namespace NexoRuta.Application.Operacion.UseCases;

public sealed class ConsultarHistorialOperativoUseCase(
    IUsuarioActual usuarioActual,
    IHistorialOperativoRepository historial,
    IEstadoProyeccionOperativaRepository estado)
{
    private static readonly HashSet<string> Tipos = ["Envio", "Bulto", "Posicion", "Ruta", "Parada", "Estimacion", "Flota"];

    public async Task<ResultadoHistorialOperativo> EjecutarAsync(
        Guid operatorId, DateTimeOffset from, DateTimeOffset to, Guid? shipmentId,
        string? type, int offset, int limit, CancellationToken ct = default)
    {
        ValidarConsulta(operatorId, from, to);
        if (shipmentId == Guid.Empty || offset is < 0 or > 10000 || limit is < 1 or > 100)
            throw new ArgumentException("El envío, offset o límite de página no es válido.");
        if (type is not null && !Tipos.Contains(type))
            throw new ArgumentException("El tipo de evento no está admitido.");

        var actor = await usuarioActual.ObtenerAsync(ct);
        var scope = ResolverAmbito(actor, operatorId);
        var before = await estado.ReadAsync(operatorId, scope.CommerceId, ct);
        var rows = await historial.QueryAsync(operatorId, scope.CommerceId, shipmentId, type,
            from.ToUniversalTime(), to.ToUniversalTime(), offset, limit, ct);
        var after = await estado.ReadAsync(operatorId, scope.CommerceId, ct);

        return new(Estado(before, after), after.VerificadaEnUtc, PoliticaHistorialOperativo.RetencionDias,
            offset, limit, rows);
    }

    internal static void ValidarConsulta(Guid operatorId, DateTimeOffset from, DateTimeOffset to)
    {
        if (operatorId == Guid.Empty || from == default || to == default || from >= to
            || to - from > TimeSpan.FromDays(31))
            throw new ArgumentException("El operador y un rango temporal de hasta 31 días son obligatorios.");
    }

    internal static (Guid OperatorId, Guid? CommerceId) ResolverAmbito(ContextoUsuario actor, Guid requestedOperator)
    {
        if (actor.ComercioId is { } commerceId)
        {
            if (commerceId == Guid.Empty || requestedOperator == Guid.Empty)
                throw new AccesoActualNoDisponibleException();
            return (requestedOperator, commerceId);
        }

        if (actor.OperadorId is not { } operatorId || operatorId == Guid.Empty || operatorId != requestedOperator)
            throw new AccesoNoPermitidoException();

        return (operatorId, null);
    }

    internal static string Estado(EstadoProyeccionOperativa before, EstadoProyeccionOperativa after)
    {
        if (before.Estado == "Indisponible" || after.Estado == "Indisponible") return "Indisponible";
        return before.Estado == "Actualizada" && after.Estado == "Actualizada"
            && before.Capturados == after.Capturados && before.Documentados == after.Documentados
                ? "Actualizada" : "Atrasada";
    }
}
