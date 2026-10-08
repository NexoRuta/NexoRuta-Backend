using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios;

public sealed class CrearEnvioUseCase(IEnviosRepository repository)
{
    public async Task<EnvioCreado> EjecutarAsync(
        CrearEnvioCommand command,
        CancellationToken cancellationToken = default)
    {
        var contexto = await repository.ObtenerContextoDemoAsync(cancellationToken)
            ?? throw new InvalidOperationException("No existe el usuario de demo vinculado a un comercio.");

        var destinatario = new Destinatario(contexto.OperadorId, command.DestinatarioNombre);
        var direccion = new Direccion(contexto.OperadorId, command.Direccion);
        var envio = new Envio(
            contexto.OperadorId,
            contexto.OperadorComercioId,
            contexto.UsuarioId,
            destinatario.Id,
            direccion.Id);
        var bulto = new Bulto(
            contexto.OperadorId,
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
            envio.OperadorComercioId,
            envio.CreadoPorUsuarioId,
            contexto.UsuarioEmail,
            contexto.OperadorNombre,
            contexto.ComercioNombre,
            bulto.Codigo,
            bulto.PesoGramos,
            bulto.LargoCentimetros,
            bulto.AnchoCentimetros,
            bulto.AltoCentimetros);
    }
}
