using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.Dtos;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Domain.Envios;
using NexoRuta.Application.Envios.Exceptions;
using NexoRuta.Application.Envios.Importacion;

namespace NexoRuta.Application.Envios.UseCases;

public sealed class CrearEnvioUseCase(IEnviosRepository repository, IUsuarioActual usuarioActual, IOperadoresRepository operadores)
{
    public async Task<EnvioCreado> EjecutarAsync(
        CrearEnvioCommand command,
        CancellationToken cancellationToken = default)
    {
        var admission = await ObtenerAdmisionAsync(command.OperadorId, cancellationToken);
        var prepared = CrearAgregado(admission, command);
        await repository.GuardarAsync(prepared.Envio, prepared.Destinatario, prepared.Direccion, prepared.Bultos, cancellationToken);
        return Resultado(prepared);
    }

    public async Task<IReadOnlyList<EnvioCreado>> EjecutarImportadosAsync(
        IReadOnlyList<SolicitudEnvioImportado> solicitudes,
        CancellationToken cancellationToken = default)
    {
        if (solicitudes.Count == 0)
            return [];
        var operadorIds = solicitudes.Select(x => x.Datos.OperadorId).Distinct().ToArray();
        if (operadorIds.Length != 1 || operadorIds[0] == Guid.Empty)
            throw new ArgumentException("Una importación debe seleccionar un único operador.", nameof(solicitudes));

        var admission = await ObtenerAdmisionAsync(operadorIds[0], cancellationToken);
        var prepared = solicitudes.Select(solicitud =>
        {
            var aggregate = CrearAgregado(admission, solicitud.Datos);
            return (Solicitud: solicitud, Aggregate: aggregate);
        }).ToArray();

        await repository.GuardarImportadosAsync(prepared.Select(x => new EnvioImportadoParaPersistir(
            x.Aggregate.Envio, x.Aggregate.Destinatario, x.Aggregate.Direccion, x.Aggregate.Bultos,
            x.Solicitud.ReferenciaExterna, x.Solicitud.VersionHuella, x.Solicitud.Huella)).ToArray(), cancellationToken);
        return prepared.Select(x => Resultado(x.Aggregate)).ToArray();
    }

    private async Task<Admission> ObtenerAdmisionAsync(Guid operadorId, CancellationToken cancellationToken)
    {
        var contexto = await usuarioActual.ObtenerAsync(cancellationToken);
        var comercio = contexto.RequerirComercio();
        if (operadorId == Guid.Empty)
            throw new ArgumentException("Seleccioná un operador para el envío.", nameof(operadorId));
        var operador = await operadores.ObtenerAsync(operadorId, cancellationToken)
            ?? throw new OperadorNoEncontradoException();
        var versionReglas = await repository.ObtenerVersionReglasParaAdmisionAsync(operador.OperadorId, cancellationToken)
            ?? throw new ReglasEnvioNoDisponiblesException();
        return new Admission(contexto, comercio, operador, versionReglas);
    }

    private static PreparedShipment CrearAgregado(Admission admission, CrearEnvioCommand command)
    {
        var destinatario = new Destinatario(admission.Operador.OperadorId, command.DestinatarioNombre, command.DestinatarioEmail);
        var direccion = new Direccion(admission.Operador.OperadorId, command.Direccion);
        var envio = new Envio(
            operadorId: admission.Operador.OperadorId,
            comercioId: admission.Comercio.Id,
            creadoPorUsuarioId: admission.Contexto.UsuarioId,
            destinatarioId: destinatario.Id,
            direccionId: direccion.Id,
            versionReglasId: admission.VersionReglas);
        if (command.Bultos is null || command.Bultos.Count == 0 || command.Bultos.Any(b => b is null))
            throw new ArgumentException("El envío requiere al menos un bulto.");
        var bultos = command.Bultos.Select(b => new Bulto(admission.Operador.OperadorId, envio.Id,
            b.Codigo, b.PesoGramos, b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros)).ToArray();
        if (bultos.Select(b => b.Codigo).Distinct(StringComparer.Ordinal).Count() != bultos.Length)
            throw new ArgumentException("Los códigos de bulto no pueden repetirse dentro del envío.");
        return new PreparedShipment(envio, destinatario, direccion, bultos, admission);
    }

    private static EnvioCreado Resultado(PreparedShipment prepared)
        => new(
            Id: prepared.Envio.Id,
            Origen: new OrigenEnvio(
                OperadorId: prepared.Envio.OperadorId,
                ComercioId: prepared.Envio.ComercioId,
                CreadoPorUsuarioId: prepared.Envio.CreadoPorUsuarioId,
                UsuarioEmail: prepared.Admission.Contexto.UsuarioEmail,
                OperadorNombre: prepared.Admission.Operador.Nombre,
                ComercioNombre: prepared.Admission.Comercio.Nombre),
            Bultos: prepared.Bultos.Select(b => new BultoDetalle(b.Codigo, b.PesoGramos,
                b.LargoCentimetros, b.AnchoCentimetros, b.AltoCentimetros)
                { Custodia = b.Custodia.ToString() }).ToArray(),
            CreadoEnUtc: prepared.Envio.CreadoEnUtc);

    private sealed record Admission(
        NexoRuta.Application.Administracion.Context.ContextoUsuario Contexto,
        (Guid Id, string Nombre) Comercio,
        NexoRuta.Application.Administracion.Dtos.OperadorDisponible Operador,
        Guid VersionReglas);

    private sealed record PreparedShipment(
        Envio Envio,
        Destinatario Destinatario,
        Direccion Direccion,
        IReadOnlyList<Bulto> Bultos,
        Admission Admission);
}
