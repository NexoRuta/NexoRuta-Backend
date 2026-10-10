using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Domain.Envios;

namespace NexoRuta.Application.Envios.UseCases;

public sealed class CrearEnvioUseCase(IEnviosRepository repository, IUsuarioActual usuarioActual, IAccesosUsuarioRepository cuentas)
{
    public async Task<EnvioCreado> EjecutarAsync(
        CrearEnvioCommand command,
        CancellationToken cancellationToken = default)
    {
        var contexto = await usuarioActual.ObtenerAsync(cancellationToken);
        var comercio = contexto.RequerirComercio();
        if (command.OperadorId == Guid.Empty)
            throw new ArgumentException("Seleccioná un operador para el envío.", nameof(command.OperadorId));
        var operador = await cuentas.ObtenerOperadorAsync(command.OperadorId, cancellationToken)
            ?? throw new OperadorNoDisponibleException();

        var destinatario = new Destinatario(operador.OperadorId, command.DestinatarioNombre);
        var direccion = new Direccion(operador.OperadorId, command.Direccion);
        var envio = new Envio(
            operadorId: operador.OperadorId,
            comercioId: comercio.Id,
            creadoPorUsuarioId: contexto.UsuarioId,
            destinatarioId: destinatario.Id,
            direccionId: direccion.Id);
        var bulto = new Bulto(
            operadorId: operador.OperadorId,
            envioId: envio.Id,
            codigo: command.CodigoBulto,
            pesoGramos: command.PesoGramos,
            largoCentimetros: command.LargoCentimetros,
            anchoCentimetros: command.AnchoCentimetros,
            altoCentimetros: command.AltoCentimetros);

        await repository.GuardarAsync(envio, destinatario, direccion, bulto, cancellationToken);

        return new EnvioCreado(
            Id: envio.Id,
            Origen: new OrigenEnvio(
                OperadorId: envio.OperadorId,
                ComercioId: envio.ComercioId,
                CreadoPorUsuarioId: envio.CreadoPorUsuarioId,
                UsuarioEmail: contexto.UsuarioEmail,
                OperadorNombre: operador.Nombre,
                ComercioNombre: comercio.Nombre),
            Bulto: new BultoDetalle(
                Codigo: bulto.Codigo,
                PesoGramos: bulto.PesoGramos,
                LargoCentimetros: bulto.LargoCentimetros,
                AnchoCentimetros: bulto.AnchoCentimetros,
                AltoCentimetros: bulto.AltoCentimetros));
    }
}
