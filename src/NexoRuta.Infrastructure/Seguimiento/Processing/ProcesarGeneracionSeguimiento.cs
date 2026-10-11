
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
            await db.Database.BeginTransactionAsync(cancellationToken);

        // 1. Serializar el procesamiento por envío.
        // PostgreSQL mantiene el bloqueo hasta COMMIT o ROLLBACK.
        var envios = await db.Database.SqlQuery<Guid>(
            $"""
            SELECT "Id" AS "Value"
            FROM "Envios"
            WHERE "Id" = {envioId}
              AND "OperadorId" = {operadorId}
            FOR UPDATE
            """)
            .ToListAsync(cancellationToken);

        if (envios.Count != 1)
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
