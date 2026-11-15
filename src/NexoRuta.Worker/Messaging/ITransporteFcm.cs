using FirebaseAdmin.Messaging;

namespace NexoRuta.Worker.Messaging;

public interface ITransporteFcm
{
    Task<IReadOnlyList<ResultadoEnvioPush>> EnviarAsync(MulticastMessage mensaje, CancellationToken ct);
}

public enum EstadoEnvioPush { Aceptado, RegistroInvalido, Reintentar }

public enum CategoriaFalloPush
{
    Transitorio,
    Cuota,
    Mensaje,
    Configuracion,
    Simulacion,
    Desconocido
}

public sealed record ResultadoEnvioPush(
    EstadoEnvioPush Estado,
    string? IdProveedor = null,
    CategoriaFalloPush Categoria = CategoriaFalloPush.Desconocido,
    string? CodigoError = null)
{
    public static ResultadoEnvioPush DesdeErrorDeLote(FirebaseMessagingException excepcion)
        => DesdeCodigoErrorDeLote(excepcion.MessagingErrorCode);

    public static ResultadoEnvioPush DesdeCodigoErrorDeLote(MessagingErrorCode? codigo)
        => codigo == MessagingErrorCode.Unregistered
            ? new(EstadoEnvioPush.Reintentar, Categoria: CategoriaFalloPush.Desconocido, CodigoError: "UnregisteredBatch")
            : DesdeCodigoError(codigo);

    public static ResultadoEnvioPush DesdeError(FirebaseMessagingException? excepcion)
        => DesdeCodigoError(excepcion?.MessagingErrorCode);

    public static ResultadoEnvioPush DesdeCodigoError(MessagingErrorCode? codigo)
    {
        if (codigo is null)
            return new(EstadoEnvioPush.Reintentar, Categoria: CategoriaFalloPush.Desconocido, CodigoError: "Unknown");

        if (codigo == MessagingErrorCode.Unregistered)
            return new(EstadoEnvioPush.RegistroInvalido, CodigoError: "Unregistered");

        var categoria = codigo switch
        {
            MessagingErrorCode.InvalidArgument => CategoriaFalloPush.Mensaje,
            MessagingErrorCode.SenderIdMismatch or MessagingErrorCode.ThirdPartyAuthError => CategoriaFalloPush.Configuracion,
            MessagingErrorCode.QuotaExceeded => CategoriaFalloPush.Cuota,
            MessagingErrorCode.Internal or MessagingErrorCode.Unavailable => CategoriaFalloPush.Transitorio,
            _ => CategoriaFalloPush.Desconocido
        };

        // InvalidArgument también puede señalar un payload inválido. Solo Unregistered
        // permite retirar un FID del registro de instalaciones.
        return new(EstadoEnvioPush.Reintentar, Categoria: categoria, CodigoError: codigo?.ToString() ?? "Unknown");
    }
}
