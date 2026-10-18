
using Microsoft.EntityFrameworkCore;
using NexoRuta.Application.Seguimiento.Services;
using NexoRuta.Application.Seguimiento.UseCases;
using NexoRuta.Infrastructure.Outbox;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Persistence;
using NexoRuta.Infrastructure.Seguimiento.Security;

namespace NexoRuta.Infrastructure.Seguimiento.Processing;

public sealed class ProcesarGeneracionSeguimiento(
    NexoRutaDbContext db,
    CrearEnlaceSeguimientoUseCase crearEnlace,
    CifradorTokenSeguimiento cifrador)
{
    public const string NombreConsumidor = "GenerarSeguimiento";

    public async Task EjecutarAsync(
        Guid messageId,
        Guid operadorId,
        Guid envioId,
        CancellationToken cancellationToken = default)
    {
        if (messageId == Guid.Empty ||
            operadorId == Guid.Empty ||
            envioId == Guid.Empty)
        {
            throw new ArgumentException(
                "Los identificadores del mensaje, operador y envío son obligatorios.");
        }

        await using var transaccion =
            await db.BeginOrJoinAsync(cancellationToken);

        var verifiedJob = await db.Database.SqlQuery<bool>($"""
            SELECT nexoruta_app.begin_tracking_job({messageId}, {operadorId}, {envioId}) AS "Value"
            """).SingleAsync(cancellationToken);
        if (!verifiedJob)
            throw new InvalidOperationException("The persisted tracking job does not match this message.");
        await db.ApplyAsync(RlsActorContext.Worker(operadorId, messageId, envioId), cancellationToken);

        // ponytail: a 64-bit hash collision only serializes unrelated shipments; it never grants row visibility.
        var lockKey = $"{operadorId:N}:{envioId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))",
            cancellationToken);

        // 1. Validate the exact shipment through its RLS-scoped SELECT policy.
        if (!await db.Envios.AsNoTracking().AnyAsync(
                x => x.Id == envioId && x.OperadorId == operadorId,
                cancellationToken))
        {
            throw new InvalidOperationException(
                "No existe el envío para el operador indicado.");
        }

        // 2. Idempotencia técnica:
        // el mismo MessageId no se procesa dos veces.
        var yaProcesado = await db.ProcessedMessages
            .AnyAsync(
                x => x.MessageId == messageId &&
                     x.Consumer == NombreConsumidor,
                cancellationToken);

        if (yaProcesado)
        {
            await transaccion.CommitAsync(cancellationToken);
            return;
        }

        // 3. Idempotencia de negocio:
        // solo una entrega inicial automática por envío.
        var entregaExistente = await db.EntregasSeguimiento
            .AnyAsync(
                x => x.OperadorId == operadorId &&
                     x.EnvioId == envioId,
                cancellationToken);

        if (!entregaExistente)
        {
            // 4. Reutilizar el caso de uso de Application.
            // GuardarAsync utiliza el mismo DbContext y transacción.
            var token = await crearEnlace.EjecutarAsync(
                operadorId,
                envioId,
                cancellationToken);

            var tokenHash =
                GeneradorTokenSeguimiento.CalcularHash(token);

            // 5. Proteger el token original.
            var protegido = cifrador.Cifrar(
                token,
                operadorId,
                envioId,
                tokenHash);

            db.EntregasSeguimiento.Add(
                new EntregaSeguimiento(
                    operadorId,
                    envioId,
                    tokenHash,
                    protegido.TokenCifrado,
                    protegido.Nonce,
                    protegido.Tag));
        }

        // 6. Registrar que este mensaje se procesó.
        db.ProcessedMessages.Add(
            new ProcessedMessage(
                messageId,
                NombreConsumidor));

        await db.SaveChangesAsync(cancellationToken);

        // 7. Confirmar todas las escrituras juntas.
        await transaccion.CommitAsync(cancellationToken);
    }
}
