using NexoRuta.Application.Administracion;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios;

public sealed class CrearEnvioUseCase(IEnviosRepository repository, IUsuarioActual usuarioActual, IAccesosUsuarioRepository cuentas)
{
    public async Task<EnvioCreado> EjecutarAsync(
        CrearEnvioCommand command,
        CancellationToken cancellationToken = default)
    {
        var contexto = await usuarioActual.ObtenerAsync(cancellationToken);
        var comercioId = contexto.ComercioId ?? throw new AccesoNoPermitidoException();
        if (command.OperadorId == Guid.Empty)
            throw new ArgumentException("Seleccioná un operador para el envío.", nameof(command.OperadorId));
        var operador = await cuentas.ObtenerOperadorAsync(command.OperadorId, cancellationToken)
            ?? throw new OperadorNoDisponibleException();

        var destinatario = new Destinatario(operador.OperadorId, command.DestinatarioNombre);
        var direccion = new Direccion(operador.OperadorId, command.Direccion);
        var envio = new Envio(
            operador.OperadorId,
            comercioId,
            contexto.UsuarioId,
            destinatario.Id,
            direccion.Id);
        var bulto = new Bulto(
            operador.OperadorId,
            envio.Id,
            command.CodigoBulto,
            command.PesoGramos,
            command.LargoCentimetros,
            command.AnchoCentimetros,
            command.AltoCentimetros);

        await repository.GuardarAsync(envio, destinatario, direccion, bulto, cancellationToken);

        return new EnvioCreado(
            envio.Id,
            envio.OperadorId,
            envio.ComercioId,
            envio.CreadoPorUsuarioId,
            contexto.UsuarioEmail,
            operador.Nombre,
            contexto.ComercioNombre!,
            bulto.Codigo,
            bulto.PesoGramos,
            bulto.LargoCentimetros,
            bulto.AnchoCentimetros,
            bulto.AltoCentimetros);
    }
}
