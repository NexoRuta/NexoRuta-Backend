using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Domain.Administracion;

namespace NexoRuta.Application.Envios.Devoluciones;

public sealed class GestionarDevolucionesUseCase(
    IUsuarioActual usuarioActual,
    IPermisosUsuarioRepository permisos,
    IDevolucionesRepository devoluciones)
{
    public async Task<PaginaDevolucionesDatos> ListarAsync(Guid? operadorId, DateTimeOffset desdeUtc,
        DateTimeOffset hastaUtc, string? situacion, string? referencia, string? cursor, int limite,
        CancellationToken ct)
    {
        var actor = await RequerirComercioAsync("envios.consultar", ct);
        if (operadorId == Guid.Empty) throw new ArgumentException("El operador indicado no es válido.");
        ValidarRango(desdeUtc, hastaUtc);
        if (limite is < 1 or > 100) throw new ArgumentException("El límite debe estar entre 1 y 100.");
        if (referencia?.Trim().Length > 128) throw new ArgumentException("La referencia admite hasta 128 caracteres.");
        return await devoluciones.ListarAsync(actor, operadorId, desdeUtc.ToUniversalTime(), hastaUtc.ToUniversalTime(),
            NormalizarSituacion(situacion), referencia?.Trim(), cursor, limite, ct);
    }

    public async Task<DevolucionDetalleDatos?> ObtenerAsync(Guid operadorId, Guid envioId, int desdeBulto, int limite,
        CancellationToken ct)
    {
        var actor = await RequerirComercioAsync("envios.consultar", ct);
        if (operadorId == Guid.Empty || envioId == Guid.Empty) throw new ArgumentException("El operador y el envío son obligatorios.");
        if (desdeBulto < 0 || limite is < 1 or > 100) throw new ArgumentException("La página de bultos no es válida.");
        var puedeAcusar = await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, "devoluciones.gestionar", ct);
        return await devoluciones.ObtenerAsync(actor, operadorId, envioId, desdeBulto, limite, puedeAcusar, ct);
    }

    public async Task<IReadOnlyList<HechoDevolucionDatos>?> HistorialAsync(Guid operadorId, Guid envioId,
        CancellationToken ct)
    {
        var actor = await RequerirComercioAsync("envios.consultar", ct);
        if (operadorId == Guid.Empty || envioId == Guid.Empty) throw new ArgumentException("El operador y el envío son obligatorios.");
        return await devoluciones.HistorialAsync(actor, operadorId, envioId, ct);
    }

    public async Task<TransferenciaDevolucionResultadoDatos?> TransferirAsync(Guid envioId,
        TransferirDevolucionComando comando, CancellationToken ct)
    {
        var actor = await RequerirOperadorAsync("envios.gestionar", ct);
        ValidarTransferencia(comando);
        return await devoluciones.TransferirAsync(actor, envioId, comando, ct);
    }

    public async Task<AcuseDevolucionResultadoDatos?> AcusarAsync(Guid operadorId, Guid transferenciaId,
        AcusarDevolucionComando comando, CancellationToken ct)
    {
        var actor = await RequerirComercioAsync("devoluciones.gestionar", ct);
        ValidarAcuse(comando);
        return await devoluciones.AcusarAsync(actor, operadorId, transferenciaId, comando, ct);
    }

    private async Task<ContextoUsuario> RequerirComercioAsync(string permiso, CancellationToken ct)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        if (actor.TipoAcceso != TipoAccesoUsuario.Comercio || actor.ComercioId is not { } comercio || comercio == Guid.Empty
            || actor.OperadorId is not null || actor.UsuarioId == Guid.Empty || actor.AccesoId == Guid.Empty
            || actor.Estado != EstadoOrganizacion.Activa)
            throw new AccesoNoPermitidoException();
        if (!await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, permiso, ct))
            throw new AccesoNoPermitidoException();
        return actor;
    }

    private async Task<ContextoUsuario> RequerirOperadorAsync(string permiso, CancellationToken ct)
    {
        var actor = await usuarioActual.ObtenerAsync(ct);
        if (actor.TipoAcceso != TipoAccesoUsuario.Operador || actor.OperadorId is not { } operador || operador == Guid.Empty
            || actor.ComercioId is not null || actor.UsuarioId == Guid.Empty || actor.AccesoId == Guid.Empty
            || actor.Estado != EstadoOrganizacion.Activa)
            throw new AccesoNoPermitidoException();
        if (!await permisos.TienePermisoAsync(actor.UsuarioId, actor.AccesoId, permiso, ct))
            throw new AccesoNoPermitidoException();
        return actor;
    }

    private static void ValidarRango(DateTimeOffset desde, DateTimeOffset hasta)
    {
        var rango = hasta.ToUniversalTime() - desde.ToUniversalTime();
        if (desde == default || hasta == default || hasta <= desde || rango > TimeSpan.FromDays(90))
            throw new ArgumentException("Usá un período UTC válido de hasta 90 días.");
    }

    private static string? NormalizarSituacion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized is not ("EnRetorno" or "EnDeposito" or "PendienteAcuse" or "Recibido" or "Historico"))
            throw new ArgumentException("La situación seleccionada no es válida.");
        return normalized;
    }

    private static void ValidarTransferencia(TransferirDevolucionComando command)
    {
        if (command is null || command.ComandoId == Guid.Empty || command.VersionEnvio < 0
            || !command.ConfirmarTransferenciaFisica || command.Bultos is null || command.Bultos.Count is < 1 or > 100
            || command.Bultos.Any(x => x.BultoId == Guid.Empty || x.VersionBulto < 0)
            || command.Bultos.Select(x => x.BultoId).Distinct().Count() != command.Bultos.Count)
            throw new ArgumentException("Confirmá la transferencia física e indicá un comando y entre 1 y 100 bultos únicos con versiones vigentes.");
    }

    private static void ValidarAcuse(AcusarDevolucionComando command)
    {
        if (command is null || command.ComandoId == Guid.Empty || command.VersionTransferencia != 1
            || command.Bultos is null || command.Bultos.Count is < 1 or > 100
            || command.Bultos.Any(x => x.BultoId == Guid.Empty || x.VersionBulto < 0)
            || command.Bultos.Select(x => x.BultoId).Distinct().Count() != command.Bultos.Count)
            throw new ArgumentException("El acuse requiere un comando, la versión vigente y entre 1 y 100 bultos únicos.");
        foreach (var line in command.Bultos)
        {
            var note = line.Observacion?.Trim();
            if (note?.Length > 500 || command.Conforme && !string.IsNullOrEmpty(note)
                || !command.Conforme && note?.Length is < 10 or null)
                throw new ArgumentException("Cada diferencia requiere una observación de 10 a 500 caracteres; un acuse conforme no lleva observaciones.");
        }
    }
}
