using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Administracion.UseCases;
using NexoRuta.Application.Envios.Repositories;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Application.Reparto.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Tarifas;
using NexoRuta.Domain.ZonasCobertura;
using NexoRuta.Application.ZonasCobertura.Repositories;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.Devoluciones;
using NexoRuta.Application.Envios.Importacion;
using NetTopologySuite;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Reparto;
using NexoRuta.Infrastructure.Envios.Repositories;
using NexoRuta.Infrastructure.ZonasCobertura;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DatosDemoSeeder(
    NexoRutaDbContext db,
    IPlataformaRepository plataforma,
    IEnviosRepository envios,
    IDevolucionesRepository devoluciones,
    IRepartidoresRepository repartidores,
    IConfiguracionRepartoRepository configuracionReparto,
    IRutasRepository rutas,
    IConfiguration configuration,
    IPermisosUsuarioRepository permisosUsuario,
    IUsuariosOrganizacionRepository usuariosOrganizacion,
    IRolesRepository roles,
    IZonasCoberturaRepository zonasCobertura,
    UserManager<Usuario> identityUsers)
{
    private static readonly TimeZoneInfo ZonaHoraria = TimeZoneInfo.FindSystemTimeZoneById("America/Montevideo");
    private static readonly string[] NombresOperadores = [
        "Logística del Sur", "Envíos Rambla", "Ruta Este", "Distribuciones Centro", "Mensajería Norte"];
    private static readonly string[] NombresComercios = [
        "Almacén La Esquina", "Farmacia del Parque", "Librería Horizonte", "Tienda Sur", "Mercado Cordón"];
    private static readonly string[] Nombres = [
        "Lucía", "Martín", "Sofía", "Diego", "Valentina", "Mateo", "Camila", "Nicolás", "Florencia", "Joaquín",
        "Agustina", "Santiago", "Mariana", "Federico", "Carolina", "Andrés", "Paula", "Gabriel", "Natalia", "Bruno"];
    private static readonly string[] Apellidos = [
        "Rodríguez", "González", "Pérez", "Fernández", "Silva", "López", "Martínez", "Sosa", "Acosta", "García",
        "Méndez", "Castro", "Suárez", "Olivera", "Pereira", "Viera", "Díaz", "Correa", "Núñez", "Cabrera"];
    private static readonly string[] Calles = [
        "Av. 18 de Julio", "Bvar. España", "Canelones", "Av. Rivera", "Colonia", "Convención", "Maldonado", "Durazno",
        "Av. Italia", "Luis A. de Herrera", "Gaboto", "Rambla República del Perú"];

    private const string CuentaPropietariaCarga = "opt06-fixture-owner@nexoruta.test";
    private const string CuentaComercioCarga = "opt06-fixture-commerce@nexoruta.test";
    private const string CodigoRolCarga = "opt06-load-driver";
    private const string PasswordActoresDemo = "Demo12345!2026";
    private static readonly string[] CorreosDespachadores = ["a1@demo.nexoruta.test", "a2@demo.nexoruta.test"];
    private static readonly string[] CorreosRepartidores = ["r1@demo.nexoruta.test", "r2@demo.nexoruta.test"];
    private const string CorreoSoloConsulta = "consulta@demo.nexoruta.test";
    private static readonly JsonSerializerOptions JsonOperacion = new(JsonSerializerDefaults.Web);

    public async Task<EstadoDatosDemo> ObtenerEstadoAsync(CancellationToken ct)
    {
        var ejecucion = await db.EjecucionesSeedDemo.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == EjecucionSeedDemo.IdentificadorUnico, ct);
        return ejecucion is null ? EstadoDatosDemo.Vacio : EstadoDatosDemo.Desde(ejecucion);
    }

    public async Task<(bool YaEstabaSembrado, EstadoDatosDemo Estado)> SembrarAsync(
        ContextoPlataforma actor, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("El seed demo requiere la transacción autenticada de la plataforma.");

        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('nexoruta:demo-data-seed', 0))", ct);
        var anterior = await db.EjecucionesSeedDemo.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == EjecucionSeedDemo.IdentificadorUnico, ct);
        if (anterior is not null)
        {
            var operadoresExistentes = new List<OrganizacionDemo>(NombresOperadores.Length);
            var comerciosExistentes = new List<OrganizacionDemo>(NombresComercios.Length);
            for (var i = 0; i < NombresOperadores.Length; i++)
                operadoresExistentes.Add(await ObtenerOrganizacionFixtureAsync(TipoAccesoUsuario.Operador,
                    $"admin.operador{i + 1}@demo.nexoruta.test", NombresOperadores[i], ct,
                    requierePropietario: false)
                    ?? throw new InvalidOperationException("Falta una cuenta de operador del seed demo."));
            for (var i = 0; i < NombresComercios.Length; i++)
                comerciosExistentes.Add(await ObtenerOrganizacionFixtureAsync(TipoAccesoUsuario.Comercio,
                    $"admin.comercio{i + 1}@demo.nexoruta.test", NombresComercios[i], ct,
                    requierePropietario: false)
                    ?? throw new InvalidOperationException("Falta una cuenta de comercio del seed demo."));
            await SincronizarActoresAsync(actor, operadoresExistentes, comerciosExistentes, ct);
            await SembrarFixtureDevolucionAp04Async(comerciosExistentes[1], operadoresExistentes[0], 1, ct);
            await SembrarFixtureDevolucionAp04Async(comerciosExistentes[1], operadoresExistentes[0], 2, ct);
            await SembrarFixtureDevolucionAp04Async(comerciosExistentes[1], operadoresExistentes[0], 3, ct);
            await SembrarFixtureDevolucionAp04Async(comerciosExistentes[1], operadoresExistentes[0], 4, ct);
            return (true, EstadoDatosDemo.Desde(anterior));
        }

        var operadores = new List<OrganizacionDemo>(NombresOperadores.Length);
        var comercios = new List<OrganizacionDemo>(NombresComercios.Length);
        for (var i = 0; i < NombresOperadores.Length; i++)
        {
            await db.ApplyAsync(RlsActorContext.From(actor), ct);
            operadores.Add(await CrearOrganizacionAsync(TipoAccesoUsuario.Operador, NombresOperadores[i],
                $"admin.operador{i + 1}@demo.nexoruta.test", ct));
        }
        for (var i = 0; i < NombresComercios.Length; i++)
        {
            await db.ApplyAsync(RlsActorContext.From(actor), ct);
            comercios.Add(await CrearOrganizacionAsync(TipoAccesoUsuario.Comercio, NombresComercios[i],
                $"admin.comercio{i + 1}@demo.nexoruta.test", ct));
        }
        await SincronizarActoresAsync(actor, operadores, comercios, ct);

        var instanteActual = DateTimeOffset.UtcNow;
        var fechaLocalHoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instanteActual, ZonaHoraria).DateTime);
        var fechaReglas = AFechaUtc(fechaLocalHoy.AddDays(-14), new TimeOnly(9, 0));
        var cantidadEnvios = 0;
        var cantidadRutas = 0;
        var cantidadOperaciones = 0;
        var ordinalEnvio = 0;

        for (var operadorIndex = 0; operadorIndex < operadores.Count; operadorIndex++)
        {
            var operador = operadores[operadorIndex];
            await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
            var repartidor = await repartidores.GuardarAsync(operador.Id, operador.AccesoId,
                Nombres[operadorIndex], activo: true, version: null, ct)
                ?? throw new InvalidOperationException("No se pudo crear el perfil de reparto demo.");
            await configuracionReparto.GuardarAsync(operador.Id,
                new(0, ZonaHoraria.Id, 50, RequiereFotoIntento: false, BloquearDiscrepanciasCarga: true), ct);
            var vehiculo = await configuracionReparto.GuardarVehiculoAsync(operador.Id,
                new(Guid.Empty, $"S{(char)('A' + operadorIndex)}{(char)('A' + operadorIndex)} {1234 + operadorIndex}",
                    350_000, 12_000_000, Activo: true, Version: 0), ct)
                ?? throw new InvalidOperationException("No se pudo crear el vehículo demo.");
            await rutas.ConfirmarFlotaAsync(operador.Contexto, vehiculo.Id, "verificar-inicial",
                new(Guid.CreateVersion7(), null, vehiculo.Version, "Verificación explícita del vehículo sintético demo."), ct);
            await PublicarReglasAsync(operador, fechaReglas, ct);
            await PublicarServicioDemoAsync(operador, ct, calendarioCompleto: true);
            var versionReglasId = await db.ReglasOperativasOperadores.AsNoTracking()
                .Where(x => x.OperadorId == operador.Id).Select(x => x.VersionVigenteId)
                .SingleAsync(ct) ?? throw new InvalidOperationException("No se publicaron reglas operativas demo.");

            for (var diasAtras = 7; diasAtras >= 1; diasAtras--)
            {
                var fechaRuta = fechaLocalHoy.AddDays(-diasAtras);
                var horaInicio = AFechaUtc(fechaRuta, new TimeOnly(8, 15));
                var enviosDia = new List<EnvioDemo>(2);
                for (var paquete = 0; paquete < 2; paquete++)
                {
                    var comercio = comercios[(operadorIndex + 2 * (7 - diasAtras) + paquete) % comercios.Count];
                    var nombre = $"{Nombres[ordinalEnvio % Nombres.Length]} {Apellidos[(ordinalEnvio * 7) % Apellidos.Length]}";
                    var calle = Calles[ordinalEnvio % Calles.Length];
                    var direccion = $"{calle} {840 + ordinalEnvio * 7}, Montevideo";
                    var creadoEn = horaInicio.AddMinutes(-35 - paquete * 12);
                    var envio = await CrearEnvioAsync(comercio, operador, versionReglasId, ordinalEnvio,
                        nombre, direccion, creadoEn, ct);
                    enviosDia.Add(envio);
                    ordinalEnvio++;
                    cantidadEnvios++;
                }

                await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
                foreach (var envio in enviosDia)
                {
                    var recepcion = await rutas.CotejarBultoAsync(operador.Contexto, envio.Bulto.Id,
                        new(Guid.CreateVersion7(), envio.Envio.Version, envio.Bulto.Version, true,
                            envio.Bulto.PesoGramos, envio.Bulto.LargoCentimetros,
                            envio.Bulto.AnchoCentimetros, envio.Bulto.AltoCentimetros,
                            "Recepción física sintética del seed general."), ct);
                    if (recepcion?.Recepcion.EstadoEnvio != "EnDeposito")
                        throw new InvalidOperationException("No se pudo registrar la recepción sintética inicial del envío demo.");
                }

                var paradas = enviosDia.Select((envio, index) => new ParadaPlanificadaDatos(
                    envio.Envio.Id, horaInicio.AddHours(1).AddMinutes(index * 45))).ToArray();
                var hoja = await rutas.PlanificarAsync(operador.Id,
                    new(repartidor.AccesoId, vehiculo.Id, fechaRuta, paradas), ct);
                var relojDemo = new RelojDemo(horaInicio);
                var rutasDia = new EfRutasRepository(db, relojDemo);
                var despachada = await rutasDia.DespacharAsync(operador.Contexto, hoja.Id, hoja.Version, ct)
                    ?? throw new InvalidOperationException("No se pudo despachar una ruta demo.");

                var operaciones = new EfOperacionesRepartoRepository(db, relojDemo, configuration);
                var actorRepartidor = new UsuarioActualDemo(operador.Contexto);
                var jornada = new EjecutarRepartoUseCase(actorRepartidor, repartidores, operaciones);
                long secuencia = 0;
                async Task AplicarAsync(string tipo, Guid? envioId, long? version, DateTimeOffset ocurrida,
                    object datos)
                {
                    relojDemo.AvanzarA(ocurrida.AddMinutes(1));
                    var resultado = await jornada.AplicarAsync(despachada.JornadaId,
                        new(Guid.CreateVersion7(), ++secuencia, tipo, envioId, version, ocurrida,
                            JsonSerializer.SerializeToElement(datos)), ct);
                    if (resultado is null || resultado.Resultado != "Aplicada")
                        throw new InvalidOperationException($"No se pudo aplicar la operación demo {tipo}.");
                    cantidadOperaciones++;
                }

                await AplicarAsync("InicioJornada", null, null, horaInicio, new { });
                foreach (var envio in enviosDia)
                    await AplicarAsync("CargarBulto", envio.Envio.Id, 2,
                        horaInicio.AddMinutes(10 + enviosDia.IndexOf(envio) * 5), new { bultoId = envio.Bulto.Id });
                await AplicarAsync("ConfirmarCarga", null, null, horaInicio.AddMinutes(25), new { });

                var fallaEnSegundoEnvio = (diasAtras + operadorIndex) % 5 == 0;
                for (var index = 0; index < enviosDia.Count; index++)
                {
                    var envio = enviosDia[index];
                    var momentoEntrega = horaInicio.AddMinutes(85 + index * 45);
                    if (fallaEnSegundoEnvio && index == 1)
                    {
                        await AplicarAsync("IntentoFallido", envio.Envio.Id, 3, momentoEntrega,
                            new { motivo = "DESTINATARIO_AUSENTE" });
                        await AplicarAsync("Rendicion", null, null, momentoEntrega.AddMinutes(12),
                            new { bultoIds = new[] { envio.Bulto.Id } });
                        var versionActual = await db.Envios.AsNoTracking()
                            .Where(x => x.OperadorId == operador.Id && x.Id == envio.Envio.Id)
                            .Select(x => x.Version).SingleAsync(ct);
                        if (!await rutas.RecibirAsync(operador.Id, envio.Envio.Id, versionActual, ct))
                            throw new InvalidOperationException("No se pudo registrar el retorno de un envío demo.");
                    }
                    else
                    {
                        await AplicarAsync("EntregaFisica", envio.Envio.Id, 3, momentoEntrega,
                            new
                            {
                                nombre = envio.Destinatario.Nombre,
                                latitud = envio.Latitud,
                                longitud = envio.Longitud,
                                precisionMetros = 12,
                                posicionEnUtc = momentoEntrega
                            });
                    }
                }

                await AplicarAsync("FinJornada", null, null, horaInicio.AddHours(8), new { });
                var ocupacion = await db.OcupacionesRuta.SingleAsync(o => o.RutaId == hoja.Id, ct);
                await rutasDia.ConfirmarFlotaAsync(operador.Contexto, vehiculo.Id, "retorno",
                    new(Guid.CreateVersion7(), ocupacion.Id, ocupacion.Version, "Retorno sintético demo."), ct);
                await rutasDia.ConfirmarFlotaAsync(operador.Contexto, vehiculo.Id, "liberar",
                    new(Guid.CreateVersion7(), ocupacion.Id, ocupacion.Version, "Carga sintética resuelta; disponibilidad demo."), ct);
                cantidadRutas++;
            }
        }

        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        await SembrarFixtureDevolucionAp04Async(comercios[1], operadores[0], 1, ct);
        await SembrarFixtureDevolucionAp04Async(comercios[1], operadores[0], 2, ct);
        await SembrarFixtureDevolucionAp04Async(comercios[1], operadores[0], 3, ct);
        await SembrarFixtureDevolucionAp04Async(comercios[1], operadores[0], 4, ct);

        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        var resultado = new EjecucionSeedDemo(DateTimeOffset.UtcNow, operadores.Count, comercios.Count,
            operadores.Count, comercios.Count, cantidadEnvios, cantidadRutas, cantidadOperaciones);
        db.EjecucionesSeedDemo.Add(resultado);
        await db.SaveChangesAsync(ct);
        return (false, EstadoDatosDemo.Desde(resultado));
    }

    public async Task<IReadOnlyList<CuentaFixtureCargaSembrada>> SembrarFixtureCargaAsync(
        ContextoPlataforma actor, IReadOnlyList<CredencialFixtureCarga> cuentas, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("El fixture de carga requiere la transacciÃ³n autenticada de la plataforma.");
        if (cuentas.Count is < 1 or > 5 || cuentas.Select(x => x.Email).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cuentas.Count)
            throw new ArgumentException("Cada lote debe contener entre una y cinco cuentas sintÃ©ticas Ãºnicas.");
        foreach (var cuenta in cuentas)
        {
            if (!Regex.IsMatch(cuenta.Email, @"^opt06-load-(?:00[1-9]|0[1-4][0-9]|050)@nexoruta\.test$", RegexOptions.IgnoreCase)
                || cuenta.Password.Length < 12)
                throw new ArgumentException("El fixture solo admite las cuentas opt06-load-001..050@nexoruta.test y contraseÃ±as vÃ¡lidas.");
        }

        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('nexoruta:opt06:load-fixture', 0))", ct);
        var operador = await ObtenerOrganizacionFixtureAsync(TipoAccesoUsuario.Operador, CuentaPropietariaCarga,
            "Operador sintÃ©tico OPT-06", ct)
            ?? await CrearOrganizacionFixtureAsync(TipoAccesoUsuario.Operador, "Operador sintÃ©tico OPT-06",
                CuentaPropietariaCarga, actor, ct);
        var comercio = await ObtenerOrganizacionFixtureAsync(TipoAccesoUsuario.Comercio, CuentaComercioCarga,
            "Comercio sintÃ©tico OPT-06", ct)
            ?? await CrearOrganizacionFixtureAsync(TipoAccesoUsuario.Comercio, "Comercio sintÃ©tico OPT-06",
                CuentaComercioCarga, actor, ct);

        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        var rolCarga = await db.Roles.SingleOrDefaultAsync(x => x.OperadorId == operador.Id && x.Codigo == CodigoRolCarga, ct);
        if (rolCarga is null)
        {
            rolCarga = new Rol(CodigoRolCarga, "Repartidor de pruebas OPT-06", operador.Id, null);
            await roles.CrearAsync(rolCarga, ct);
        }

        if (!await roles.ActualizarPermisosAsync(operador.Contexto, rolCarga.Id, ["reparto.ejecutar"], ct))
            throw new InvalidOperationException("OPT-06 role permissions could not be assigned.");

        if (await configuracionReparto.ObtenerAsync(operador.Id, ct) is null)
            await configuracionReparto.GuardarAsync(operador.Id,
                new(0, ZonaHoraria.Id, 50, RequiereFotoIntento: false, BloquearDiscrepanciasCarga: true), ct);
        if (!await db.ReglasOperativasOperadores.AnyAsync(x => x.OperadorId == operador.Id, ct))
            await PublicarReglasAsync(operador, DateTimeOffset.UtcNow, ct);
        if (!await db.ZonasCobertura.AnyAsync(x => x.OperadorId == operador.Id, ct))
            await PublicarServicioDemoAsync(operador, ct, calendarioCompleto: true);
        var versionReglasId = await db.ReglasOperativasOperadores.AsNoTracking()
            .Where(x => x.OperadorId == operador.Id).Select(x => x.VersionVigenteId)
            .SingleAsync(ct) ?? throw new InvalidOperationException("No se publicaron reglas operativas para el fixture OPT-06.");

        var resultado = new List<CuentaFixtureCargaSembrada>(cuentas.Count);
        foreach (var credencial in cuentas)
        {
            ct.ThrowIfCancellationRequested();
            var repartidor = await ObtenerOCrearCuentaCargaAsync(operador, rolCarga, credencial, ct);
            await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
            var perfil = await db.Repartidores.SingleOrDefaultAsync(x => x.OperadorId == operador.Id
                && x.AccesoId == repartidor.AccesoId, ct);
            var numeroCuenta = credencial.Email.Split('@')[0]["opt06-load-".Length..];
            var nombre = $"Repartidor OPT-06 {numeroCuenta}";
            if (perfil is null)
                await repartidores.GuardarAsync(operador.Id, repartidor.AccesoId, nombre, activo: true, version: null, ct);
            else if (!perfil.Activo)
                await repartidores.GuardarAsync(operador.Id, repartidor.AccesoId, nombre, activo: true, perfil.Version, ct);

            var matricula = $"OTL{numeroCuenta[^3..]}";
            var vehiculos = await configuracionReparto.VehiculosAsync(operador.Id, ct);
            var vehiculo = vehiculos.SingleOrDefault(x => x.Matricula == matricula)
                ?? await configuracionReparto.GuardarVehiculoAsync(operador.Id,
                    new(Guid.Empty, matricula, 350_000, 12_000_000, Activo: true, Version: 0), ct)
                ?? throw new InvalidOperationException("No se pudo crear el vehÃ­culo sintÃ©tico OPT-06.");
            if (!vehiculo.Activo)
                vehiculo = await configuracionReparto.GuardarVehiculoAsync(operador.Id,
                    vehiculo with { Activo = true }, ct)
                    ?? throw new InvalidOperationException("OPT-06 synthetic vehicle could not be activated.");
            await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
            var entidadVehiculo = await db.Vehiculos.SingleAsync(x => x.OperadorId == operador.Id && x.Id == vehiculo.Id, ct);
            if (entidadVehiculo.VerificacionInicialEnUtc is null)
                await rutas.ConfirmarFlotaAsync(operador.Contexto, vehiculo.Id, "verificar-inicial",
                    new(Guid.CreateVersion7(), null, vehiculo.Version, "VerificaciÃ³n explÃ­cita del vehÃ­culo sintÃ©tico OPT-06."), ct);

            resultado.Add(await ObtenerOCrearJornadaFixtureAsync(operador, comercio, repartidor, vehiculo, versionReglasId, ct));
        }
        return resultado;
    }

    private async Task<OrganizacionDemo?> ObtenerOrganizacionFixtureAsync(TipoAccesoUsuario tipo, string email,
        string nombreFallback, CancellationToken ct, bool requierePropietario = true)
    {
        var usuario = await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(x => x.Email == email, ct);
        if (usuario is null) return null;
        await db.SelectPlatformTargetUserAsync(usuario.Id, ct);
        var accesos = db.AccesosUsuario.AsNoTracking().Where(x => x.UsuarioId == usuario.Id
            && x.TipoOrganizacion == tipo);
        if (requierePropietario) accesos = accesos.Where(x => x.EsPropietario);
        var acceso = await accesos.SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException(requierePropietario
                ? "La identidad reservada para el fixture OPT-06 tiene un acceso incompatible."
                : $"La cuenta demo {email} no tiene un acceso compatible con {tipo}.");
        await db.SelectPlatformTargetOrganizationAsync(acceso.OrganizacionId, ct);
        var rol = await db.Roles.AsNoTracking().SingleAsync(x => x.Id == acceso.RolId, ct);
        var nombre = tipo == TipoAccesoUsuario.Operador
            ? await db.Operadores.AsNoTracking().Where(x => x.Id == acceso.OperadorId).Select(x => x.Nombre).SingleAsync(ct)
            : await db.Comercios.AsNoTracking().Where(x => x.Id == acceso.ComercioId).Select(x => x.Nombre).SingleAsync(ct);
        var contexto = new ContextoUsuario(acceso.Id, usuario.Id, acceso.OperadorId, acceso.ComercioId, usuario.Email!,
            acceso.OperadorId is null ? null : nombre, acceso.ComercioId is null ? null : nombre,
            rol.Id, rol.Codigo, acceso.VersionSesion);
        return new(acceso.OrganizacionId, nombreFallback, usuario.Email!, usuario.Id, acceso.Id, rol.Id, contexto);
    }

    private async Task<OrganizacionDemo> CrearOrganizacionFixtureAsync(TipoAccesoUsuario tipo, string nombre,
        string email, ContextoPlataforma actor, CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        await plataforma.CrearAsync(tipo, nombre, email, CrearPasswordAleatoria(), ct);
        return await ObtenerOrganizacionFixtureAsync(tipo, email, nombre, ct)
            ?? throw new InvalidOperationException("No se pudo recuperar la organizaciÃ³n sintÃ©tica OPT-06 luego de crearla.");
    }

    private async Task<ContextoUsuario> ObtenerOCrearCuentaCargaAsync(OrganizacionDemo operador, Rol rol,
        CredencialFixtureCarga credencial, CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        var usuario = await db.Usuarios.AsNoTracking().SingleOrDefaultAsync(x => x.Email == credencial.Email, ct);
        if (usuario is null)
        {
            var creado = await new CrearUsuarioUseCase(new UsuarioActualDemo(operador.Contexto), permisosUsuario,
                usuariosOrganizacion, roles).EjecutarAsync(credencial.Email, credencial.Password, rol.Id, ct);
            usuario = await db.Usuarios.AsNoTracking().SingleAsync(x => x.Id == creado.UsuarioId, ct);
        }
        var acceso = await db.AccesosUsuario.SingleOrDefaultAsync(x => x.UsuarioId == usuario.Id
            && x.OperadorId == operador.Id, ct)
            ?? throw new InvalidOperationException("La cuenta sintÃ©tica OPT-06 pertenece a otra organizaciÃ³n.");
        if (acceso.EsPropietario)
            throw new InvalidOperationException("Una cuenta de carga OPT-06 no puede tener rol propietario.");
        if (acceso.RolId != rol.Id && !await roles.AsignarAsync(operador.Contexto, acceso.Id, rol.Id,
            puedeAsignarRolProtegido: false, ct))
            throw new InvalidOperationException("No se pudo aplicar el rol mÃ­nimo a una cuenta sintÃ©tica OPT-06.");
        if (!acceso.Activo)
        {
            acceso.CambiarEstado(true);
            await db.SaveChangesAsync(ct);
        }
        return new ContextoUsuario(acceso.Id, usuario.Id, operador.Id, null, usuario.Email!, operador.Nombre,
            null, rol.Id, rol.Codigo, acceso.VersionSesion);
    }

    private async Task<CuentaFixtureCargaSembrada> ObtenerOCrearJornadaFixtureAsync(OrganizacionDemo operador,
        OrganizacionDemo comercio, ContextoUsuario repartidor, VehiculoDatos vehiculo, Guid versionReglasId,
        CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        var hoja = await db.HojasRuta.Where(x => x.OperadorId == operador.Id
            && x.RepartidorAccesoId == repartidor.AccesoId
            && (x.Estado == EstadoHojaRuta.Despachada || x.Estado == EstadoHojaRuta.EnCurso))
            .OrderByDescending(x => x.Fecha).FirstOrDefaultAsync(ct);
        var ahora = DateTimeOffset.UtcNow;
        var reloj = new RelojDemo(ahora);
        var rutasDemo = new EfRutasRepository(db, reloj);
        if (hoja is null)
        {
            var hoy = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(ahora, ZonaHoraria).DateTime);
            var cuenta = int.Parse(repartidor.UsuarioEmail["opt06-load-".Length..^"@nexoruta.test".Length]);
            var ordinal = 100_000 + await db.Envios.CountAsync(x => x.OperadorId == operador.Id, ct) * 50 + cuenta;
            var envio = await CrearEnvioAsync(comercio, operador, versionReglasId, ordinal,
                $"Destinatario OPT-06 {ordinal}", $"Av. 18 de Julio {1000 + ordinal % 500}, Montevideo",
                ahora.AddMinutes(-10), ct);
            await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
            var recepcion = await rutas.CotejarBultoAsync(operador.Contexto, envio.Bulto.Id,
                new(Guid.CreateVersion7(), envio.Envio.Version, envio.Bulto.Version, true,
                    envio.Bulto.PesoGramos, envio.Bulto.LargoCentimetros, envio.Bulto.AnchoCentimetros,
                    envio.Bulto.AltoCentimetros, "Cotejo sintÃ©tico OPT-06."), ct);
            if (recepcion?.Recepcion.EstadoEnvio != "EnDeposito")
                throw new InvalidOperationException("No se pudo admitir el envÃ­o sintÃ©tico OPT-06 en depÃ³sito.");
            var plan = await rutas.PlanificarAsync(operador.Id, new(repartidor.AccesoId, vehiculo.Id, hoy,
                [new(envio.Envio.Id, ahora.AddMinutes(10))]), ct);
            hoja = await db.HojasRuta.SingleAsync(x => x.OperadorId == operador.Id && x.Id == plan.Id, ct);
            if (await rutasDemo.DespacharAsync(operador.Contexto, hoja.Id, hoja.Version, ct) is null)
                throw new InvalidOperationException("No se pudo despachar la jornada sintÃ©tica OPT-06.");
        }
        else if (hoja.Estado == EstadoHojaRuta.Despachada
            && await rutasDemo.DespacharAsync(operador.Contexto, hoja.Id, hoja.Version, ct) is null)
            throw new InvalidOperationException("No se pudo iniciar la jornada sintÃ©tica OPT-06 pendiente de despacho.");

        var jornada = new EjecutarRepartoUseCase(new UsuarioActualDemo(repartidor), repartidores,
            new EfOperacionesRepartoRepository(db, reloj, configuration));
        var existentes = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == operador.Id
            && x.RutaId == hoja.Id).OrderBy(x => x.Secuencia).ToArrayAsync(ct);
        if (!existentes.Any(x => x.Secuencia == 1))
        {
            reloj.AvanzarA(ahora.AddSeconds(2));
            var inicio = await jornada.AplicarAsync(hoja.JornadaId,
                new(Guid.CreateVersion7(), 1, "InicioJornada", null, null, ahora,
                    JsonSerializer.SerializeToElement(new { })), ct);
            if (inicio?.Resultado != "Aplicada")
                throw new InvalidOperationException("No se pudo registrar el inicio de la jornada sintÃ©tica OPT-06.");
        }

        existentes = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == operador.Id
            && x.RutaId == hoja.Id).OrderBy(x => x.Secuencia).ToArrayAsync(ct);
        var ultima = existentes.Select(x => x.Secuencia).DefaultIfEmpty(0).Max();
        for (var secuencia = ultima + 1; secuencia <= 51; secuencia++)
        {
            var indice = checked((int)secuencia - 2);
            var ocurrida = ahora.AddSeconds(secuencia * 2);
            reloj.AvanzarA(ocurrida.AddSeconds(1));
            var resultado = await jornada.AplicarAsync(hoja.JornadaId,
                new(Guid.CreateVersion7(), secuencia, "Posicion", null, null, ocurrida,
                    JsonSerializer.SerializeToElement(new
                    {
                        latitud = -34.90 + indice % 9 * 0.001,
                        longitud = -56.18 + indice % 11 * 0.001,
                        precisionMetros = 12,
                        posicionEnUtc = ocurrida
                    })), ct);
            if (resultado?.Resultado != "Aplicada")
                throw new InvalidOperationException("No se pudieron completar las posiciones sintÃ©ticas OPT-06.");
        }

        var payloads = await db.OperacionesJornada.AsNoTracking().Where(x => x.OperadorId == operador.Id
            && x.RutaId == hoja.Id && x.Tipo == "Posicion" && x.Secuencia >= 2 && x.Secuencia <= 51)
            .OrderBy(x => x.Secuencia).Select(x => x.Payload).ToArrayAsync(ct);
        if (payloads.Length != 50)
            throw new InvalidOperationException("La jornada OPT-06 no tiene exactamente 50 posiciones reutilizables.");
        var eventos = payloads.Select(payload => JsonSerializer.Deserialize<OperacionRepartoDatos>(payload, JsonOperacion)
            ?? throw new InvalidOperationException("Una operaciÃ³n sintÃ©tica OPT-06 no pudo serializarse.")).ToArray();
        return new(repartidor.UsuarioEmail, hoja.JornadaId, eventos);
    }

    private async Task<OrganizacionDemo> CrearOrganizacionAsync(TipoAccesoUsuario tipo, string nombre,
        string email, CancellationToken ct)
    {
        var alta = await plataforma.CrearAsync(tipo, nombre, email, PasswordActoresDemo, ct);
        var operadorId = tipo == TipoAccesoUsuario.Operador ? alta.Organizacion.Id : (Guid?)null;
        var comercioId = tipo == TipoAccesoUsuario.Comercio ? alta.Organizacion.Id : (Guid?)null;
        var contexto = new ContextoUsuario(alta.AccesoId, alta.UsuarioId, operadorId, comercioId, email,
            operadorId is null ? null : nombre, comercioId is null ? null : nombre, alta.RolId, "administrador");
        return new(alta.Organizacion.Id, nombre, email, alta.UsuarioId, alta.AccesoId, alta.RolId, contexto);
    }

    private async Task SincronizarActoresAsync(ContextoPlataforma actor,
        IReadOnlyList<OrganizacionDemo> operadores, IReadOnlyList<OrganizacionDemo> comercios, CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        // Platform credentials remain owned by PlatformInitialization/deployment secrets.
        foreach (var organizacion in operadores.Concat(comercios))
            await FijarPasswordAsync(organizacion.Email, ct);

        var operadorA = operadores[0];
        await db.ApplyAsync(RlsActorContext.From(operadorA.Contexto), ct);
        var rolesOperador = await roles.ListarAsync(operadorA.Contexto, ct);
        var rolDespachador = rolesOperador.SingleOrDefault(x => x.Codigo == CodigosRolesSistema.DespachadorOperador)
            ?? throw new InvalidOperationException("Falta el rol de despachador del operador demo A.");
        var rolRepartidor = await ObtenerOCrearRolDemoAsync(operadorA, "demo-repartidor", "Repartidor demo",
            ["reparto.ejecutar"], ct);
        var rolConsulta = await ObtenerOCrearRolDemoAsync(operadorA, "demo-solo-consulta", "Solo consulta",
            ["envios.consultar"], ct);

        for (var i = 0; i < CorreosDespachadores.Length; i++)
            await AsegurarActorAsync(operadorA, CorreosDespachadores[i], $"Despachador A{i + 1}",
                rolDespachador.Id, ct);
        for (var i = 0; i < CorreosRepartidores.Length; i++)
        {
            var email = CorreosRepartidores[i];
            var accesoId = await AsegurarActorAsync(operadorA, email, $"Repartidor R{i + 1}", rolRepartidor.Id, ct);
            var existente = (await repartidores.ListarAsync(operadorA.Id, ct))
                .SingleOrDefault(x => x.AccesoId == accesoId);
            if (await repartidores.GuardarAsync(operadorA.Id, accesoId, $"Repartidor R{i + 1}", activo: true,
                existente?.Version, ct) is null)
                throw new InvalidOperationException($"No se pudo habilitar el perfil de {email}.");
        }
        await AsegurarActorAsync(operadorA, CorreoSoloConsulta, "Solo consulta", rolConsulta.Id, ct);
    }

    private async Task<Rol> ObtenerOCrearRolDemoAsync(OrganizacionDemo operador, string codigo, string nombre,
        IReadOnlyList<string> permisos, CancellationToken ct)
    {
        var existente = (await roles.ListarAsync(operador.Contexto, ct)).SingleOrDefault(x => x.Codigo == codigo);
        var rol = existente is null
            ? new Rol(codigo, nombre, operador.Id, null)
            : await roles.ObtenerAsync(operador.Contexto, existente.Id, ct)
                ?? throw new InvalidOperationException($"No se pudo cargar el rol demo {codigo}.");
        if (existente is null) await roles.CrearAsync(rol, ct);
        if (!await roles.ActualizarPermisosAsync(operador.Contexto, rol.Id, permisos, ct))
            throw new InvalidOperationException($"No se pudieron actualizar los permisos del rol demo {codigo}.");
        return rol;
    }

    private async Task<Guid> AsegurarActorAsync(OrganizacionDemo operador, string email, string nombre,
        Guid rolId, CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        var usuario = await db.Usuarios.SingleOrDefaultAsync(x => x.Email == email, ct);
        Guid accesoId;
        if (usuario is null)
        {
            var creado = await new CrearUsuarioUseCase(new UsuarioActualDemo(operador.Contexto), permisosUsuario,
                usuariosOrganizacion, roles).EjecutarAsync(email, PasswordActoresDemo, rolId, ct);
            accesoId = creado.AccesoId;
        }
        else
        {
            var acceso = await db.AccesosUsuario.SingleOrDefaultAsync(x => x.UsuarioId == usuario.Id, ct)
                ?? throw new InvalidOperationException($"La cuenta {email} no tiene acceso de organización.");
            if (acceso.OperadorId != operador.Id || acceso.ComercioId is not null || acceso.EsPropietario)
                throw new InvalidOperationException($"La cuenta {email} ya pertenece a otro ámbito y no se modificó.");
            if (acceso.RolId != rolId && !await roles.AsignarAsync(operador.Contexto, acceso.Id, rolId,
                puedeAsignarRolProtegido: true, ct))
                throw new InvalidOperationException($"No se pudo asignar el rol demo a {email}.");
            if (!acceso.Activo)
            {
                acceso.CambiarEstado(true);
                await db.SaveChangesAsync(ct);
            }
            accesoId = acceso.Id;
        }
        await FijarPasswordAsync(email, ct);
        return accesoId;
    }

    private async Task FijarPasswordAsync(string email, CancellationToken ct)
    {
        var usuario = await db.Usuarios.SingleOrDefaultAsync(x => x.Email == email, ct)
            ?? throw new InvalidOperationException($"No se encontró la cuenta demo {email}.");
        if (!string.IsNullOrWhiteSpace(usuario.PasswordHash)
            && identityUsers.PasswordHasher.VerifyHashedPassword(usuario, usuario.PasswordHash, PasswordActoresDemo)
                == PasswordVerificationResult.Success)
            return;
        usuario.ActualizarPasswordHash(identityUsers.PasswordHasher.HashPassword(usuario, PasswordActoresDemo));
        usuario.RevocarSesionesPorCambioDePassword();
        usuario.SecurityStamp = Guid.NewGuid().ToString("N");
        usuario.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
    }

    private async Task PublicarReglasAsync(OrganizacionDemo operador, DateTimeOffset publicadoEn, CancellationToken ct)
    {
        var raiz = ReglasOperativasOperador.Crear(operador.Id, publicadoEn);
        db.ReglasOperativasOperadores.Add(raiz);
        await db.SaveChangesAsync(ct);

        var version = VersionReglasOperativas.CrearBorrador(operador.Id, operador.UsuarioId, ahoraUtc: publicadoEn);
        db.VersionesReglasOperativas.Add(version);
        await db.SaveChangesAsync(ct);
        version.ReemplazarConfiguracion(0,
            new(
                MaxIntentosEntrega: 3,
                IntervaloEntreIntentosMinutos: 120,
                PoliticaDevolucionAlAgotarIntentos:
                    PoliticaDevolucionAlAgotarIntentos.DecisionOperativa,
                PlazoEstandarMinutos: 1440,
                PlazoUrgenteMinutos: 360,
                PlazoValidacionPruebaMinutos: 1440,
                RequisitosPrueba: [TipoPruebaEntrega.NombreReceptor],
                Motivos:
                [
                    new("DESTINATARIO_AUSENTE", "Destinatario ausente", true),
                    new("DIRECCION_INCORRECTA", "Dirección incompleta", true)
                ]));
        await db.SaveChangesAsync(ct);
        version.Publicar(1, 1, operador.UsuarioId, publicadoEn);
        await db.SaveChangesAsync(ct);
        raiz.Publicar(version, publicadoEn);
        await db.SaveChangesAsync(ct);
    }

    private async Task<EnvioDemo> CrearEnvioAsync(OrganizacionDemo comercio, OrganizacionDemo operador,
        Guid versionReglasId, int ordinal, string nombre, string direccionTexto, DateTimeOffset creadoEn, CancellationToken ct)
    {
        await db.ApplyAsync(RlsActorContext.From(comercio.Contexto), ct);
        await db.SelectOperatorAsync(operador.Id, ct);
        var destinatario = new Destinatario(operador.Id, nombre,
            $"destinatario{ordinal + 1}@demo.nexoruta.test");
        var direccion = new Direccion(operador.Id, direccionTexto);
        var latitud = -34.90 + ordinal % 9 * 0.002;
        var longitud = -56.18 + ordinal % 11 * 0.002;
        var envio = new Envio(operador.Id, comercio.Id, comercio.UsuarioId, destinatario.Id, direccion.Id,
            versionReglasId, creadoEn);
        var bulto = new Bulto(operador.Id, envio.Id, $"NR-{creadoEn:yyMMdd}-{ordinal + 1:0000}",
            650 + ordinal % 8 * 420, 28 + ordinal % 4 * 5, 18 + ordinal % 3 * 4, 10 + ordinal % 4 * 3);
        await envios.GuardarAsync(envio, destinatario, direccion, [bulto],
            new ServicioEnvioDeclarado(operador.Id, latitud, longitud, "Estandar"), ct);
        return new(envio, destinatario, bulto, latitud, longitud);
    }

    private async Task SembrarFixtureDevolucionAp04Async(OrganizacionDemo comercio, OrganizacionDemo operador,
        int ordinal, CancellationToken ct)
    {
        if (ordinal is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        var sufijo = ordinal.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        var referencia = ordinal == 4 ? $"AP04-GCP-DEV-C2-{sufijo}-HIST" : $"AP04-GCP-DEV-C2-{sufijo}";
        var codigosBulto = ordinal == 3
            ? new[] { $"AP04-GCP-DEV-C2-{sufijo}-P1", $"AP04-GCP-DEV-C2-{sufijo}-P2" }
            : ordinal == 4
                ? [$"AP04-GCP-DEV-C2-{sufijo}-HIST-P1"]
                : [$"AP04-GCP-DEV-C2-{sufijo}-P1"];

        await db.ApplyAsync(RlsActorContext.From(comercio.Contexto), ct);
        await envios.BloquearReferenciasImportacionAsync([referencia], ct);
        var identidades = await envios.BuscarIdentidadesImportacionAsync([referencia], ct);
        if (identidades.Count > 1)
            throw new InvalidOperationException("La referencia reservada de la fixture AP-04 no es única.");
        if (identidades.Count == 1)
        {
            if (identidades[0].OperadorId != operador.Id)
                throw new InvalidOperationException("La referencia reservada AP-04 pertenece a otro operador.");
            await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
            var existentes = await db.Bultos.AsNoTracking().Where(x => x.OperadorId == operador.Id
                    && x.EnvioId == identidades[0].EnvioId)
                .Select(x => x.Codigo).ToArrayAsync(ct);
            if (existentes.Length != codigosBulto.Length
                || codigosBulto.Except(existentes, StringComparer.Ordinal).Any())
                throw new InvalidOperationException("La referencia AP-04 ya existe con una declaración distinta a la fixture reservada.");
            return;
        }

        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        await AsegurarRevisionActualCoberturaAp04Async(comercio, operador, ct);
        var reglasBaseId = await db.ReglasOperativasOperadores.AsNoTracking()
            .Where(x => x.OperadorId == operador.Id).Select(x => x.VersionVigenteId)
            .SingleAsync(ct) ?? throw new InvalidOperationException("El operador demo no tiene reglas publicadas para AP-04.");
        var instanteActual = DateTimeOffset.UtcNow;
        var hoyLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instanteActual, ZonaHoraria).DateTime);
        var diasDesdeLunes = ((int)hoyLocal.DayOfWeek + 6) % 7;
        // Keep the second fixture inside the default Commerce date range while avoiding
        // the seven recent route dates occupied by the general demo seed.
        var lunesMasReciente = hoyLocal.AddDays(-diasDesdeLunes - (diasDesdeLunes == 0 ? 7 : 0));
        var fechaOperativa = ordinal == 4
            // Keep the historical sample inside Commerce's default 30-day range.
            ? lunesMasReciente.AddDays(-14)
            : lunesMasReciente.AddDays(-21 * (ordinal - 1));
        var reglasId = ordinal == 3
            ? await AsegurarReglasEntregaParcialAp04Async(operador, reglasBaseId, instanteActual, ct)
            : reglasBaseId;

        var destinatario = new Destinatario(operador.Id, $"Destinatario sintético AP-04 {sufijo}",
            $"ap04-destinatario-{sufijo}@demo.nexoruta.test");
        var direccion = new Direccion(operador.Id, "Av. 18 de Julio 1200, Montevideo");
        var latitud = -34.905;
        var longitud = -56.185;
        var servicio = new ServicioEnvioDeclarado(operador.Id, latitud, longitud, "Estandar");
        var comandoAlta = new CrearEnvioCommand(operador.Id, destinatario.Nombre, destinatario.Email,
            "Av. 18 de Julio 1200, Montevideo",
            codigosBulto.Select(codigo => new BultoDeclarado(codigo, 500, 20, 15, 10)).ToArray(), servicio);
        var envio = new Envio(operador.Id, comercio.Id, comercio.UsuarioId, destinatario.Id, direccion.Id,
            reglasId, AFechaUtc(fechaOperativa, new TimeOnly(8, 45)));
        var bultos = codigosBulto.Select(codigo => new Bulto(operador.Id, envio.Id, codigo, 500, 20, 15, 10)).ToArray();
        await db.ApplyAsync(RlsActorContext.From(comercio.Contexto), ct);
        await db.SelectOperatorAsync(operador.Id, ct);
        await envios.GuardarImportadosAsync(
            [new(envio, destinatario, direccion, bultos, servicio, referencia,
                HuellaSolicitudEnvio.VersionActual,
                HuellaSolicitudEnvio.Calcular(comercio.Id, operador.Id, comandoAlta))], ct);

        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        var reloj = new RelojDemo(AFechaUtc(fechaOperativa, new TimeOnly(8, 45)));
        var rutasDemo = new EfRutasRepository(db, reloj);
        foreach (var bulto in bultos)
        {
            var versionEnvioRecepcion = await db.Envios.AsNoTracking()
                .Where(x => x.OperadorId == operador.Id && x.Id == envio.Id).Select(x => x.Version).SingleAsync(ct);
            var versionBultoRecepcion = await db.Bultos.AsNoTracking()
                .Where(x => x.OperadorId == operador.Id && x.Id == bulto.Id).Select(x => x.Version).SingleAsync(ct);
            var resultadoRecepcion = await rutasDemo.CotejarBultoAsync(operador.Contexto, bulto.Id,
                new(Guid.CreateVersion7(), versionEnvioRecepcion, versionBultoRecepcion, true, bulto.PesoGramos,
                    bulto.LargoCentimetros, bulto.AnchoCentimetros, bulto.AltoCentimetros,
                    "Recepción física sintética de la fixture AP-04."), ct);
            if (resultadoRecepcion is null)
                throw new InvalidOperationException("No se pudo cotejar un bulto de la fixture sintética AP-04 en depósito.");
        }
        var estadoAdmision = await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
            .Select(x => x.Estado.ToString()).SingleAsync(ct);
        if (estadoAdmision != "EnDeposito")
            throw new InvalidOperationException("No se pudo admitir la fixture sintética AP-04 en depósito.");

        var repartidor = await repartidores.ObtenerActivoAsync(operador.Id, operador.AccesoId, ct)
            ?? throw new InvalidOperationException("Falta el repartidor del operador demo para la fixture AP-04.");
        var vehiculo = (await configuracionReparto.VehiculosAsync(operador.Id, ct))
            .SingleOrDefault(x => x.Matricula == "SAA 1234" && x.Activo)
            ?? throw new InvalidOperationException("Falta el vehículo demo disponible para la fixture AP-04.");
        var operaciones = new EfOperacionesRepartoRepository(db, reloj, configuration);
        var jornada = new EjecutarRepartoUseCase(new UsuarioActualDemo(operador.Contexto), repartidores, operaciones);

        var cantidadIntentos = ordinal == 3 ? 1 : 3;
        for (var intento = 1; intento <= cantidadIntentos; intento++)
        {
            var horaLlegada = new TimeOnly(9, 30).Add(TimeSpan.FromHours((intento - 1) * 2.5));
            var inicio = AFechaUtc(fechaOperativa, horaLlegada.Add(TimeSpan.FromMinutes(-30)));
            var llegada = AFechaUtc(fechaOperativa, horaLlegada);
            reloj.AvanzarA(inicio);
            var hoja = await rutasDemo.PlanificarAsync(operador.Id,
                new(repartidor.AccesoId, vehiculo.Id, fechaOperativa,
                    [new(envio.Id, llegada)]), ct);
            var despachada = await rutasDemo.DespacharAsync(operador.Contexto, hoja.Id, hoja.Version, ct)
                ?? throw new InvalidOperationException("No se pudo despachar la ruta sintética AP-04.");
            long secuencia = 0;

            async Task AplicarAsync(string tipo, DateTimeOffset ocurrida, object datos, bool conEnvio)
            {
                reloj.AvanzarA(ocurrida.AddMinutes(1));
                var versionActual = conEnvio
                    ? await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
                        .Select(x => (long?)x.Version).SingleAsync(ct)
                    : null;
                var resultado = await jornada.AplicarAsync(despachada.JornadaId,
                    new(Guid.CreateVersion7(), ++secuencia, tipo, conEnvio ? envio.Id : null,
                        versionActual, ocurrida, JsonSerializer.SerializeToElement(datos)), ct);
                if (resultado is null || resultado.Resultado != "Aplicada")
                    throw new InvalidOperationException($"No se pudo aplicar la operación sintética AP-04 {tipo}.");
            }

            await AplicarAsync("InicioJornada", inicio, new { }, conEnvio: false);
            var paquetesPendientes = await db.Bultos.AsNoTracking().Where(x => x.OperadorId == operador.Id
                    && x.EnvioId == envio.Id && x.Resultado == ResultadoBulto.Pendiente)
                .OrderBy(x => x.Codigo).ToArrayAsync(ct);
            foreach (var bultoPendiente in paquetesPendientes)
                await AplicarAsync("CargarBulto", inicio.AddMinutes(10), new { bultoId = bultoPendiente.Id }, conEnvio: true);
            await AplicarAsync("ConfirmarCarga", inicio.AddMinutes(20), new { }, conEnvio: false);
            var paquetesDeRuta = await db.Bultos.AsNoTracking().Where(x => x.OperadorId == operador.Id
                    && x.EnvioId == envio.Id && x.Custodia == EstadoCustodiaBulto.Repartidor
                    && x.RutaCustodiaId == hoja.Id)
                .OrderBy(x => x.Codigo).ToArrayAsync(ct);
            Guid[] rendidos;
            if (ordinal == 3)
            {
                var versiones = await db.Bultos.AsNoTracking().Where(x => x.OperadorId == operador.Id
                        && paquetesDeRuta.Select(p => p.Id).Contains(x.Id))
                    .OrderBy(x => x.Codigo).ToArrayAsync(ct);
                await AplicarAsync("EntregaBultos", llegada, new
                {
                    resultadosBultos = versiones.Select((b, i) => new
                    {
                        bultoId = b.Id,
                        version = b.Version,
                        entregado = i == 0,
                        motivo = i == 0 ? null : "DESTINATARIO_AUSENTE"
                    }).ToArray(),
                    nombre = destinatario.Nombre
                }, conEnvio: true);
                rendidos = paquetesDeRuta.Skip(1).Select(x => x.Id).ToArray();
                if (rendidos.Length != 1)
                    throw new InvalidOperationException("La fixture parcial AP-04 requiere exactamente un bulto devuelto.");
            }
            else
            {
                await AplicarAsync("IntentoFallido", llegada,
                    new { motivo = "DESTINATARIO_AUSENTE" }, conEnvio: true);
                rendidos = paquetesDeRuta.Select(x => x.Id).ToArray();
            }
            await AplicarAsync("Rendicion", llegada.AddMinutes(5), new { bultoIds = rendidos }, conEnvio: false);
            await AplicarAsync("FinJornada", llegada.AddMinutes(10), new { }, conEnvio: false);

            var versionEnvio = await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
                .Select(x => x.Version).SingleAsync(ct);
            if (!await rutasDemo.RecibirAsync(operador.Id, envio.Id, versionEnvio, ct))
                throw new InvalidOperationException("No se pudo recibir la rendición sintética AP-04 en depósito.");

            var ocupacion = await db.OcupacionesRuta.SingleAsync(x => x.OperadorId == operador.Id && x.RutaId == hoja.Id, ct);
            await rutasDemo.ConfirmarFlotaAsync(operador.Contexto, vehiculo.Id, "retorno",
                new(Guid.CreateVersion7(), ocupacion.Id, ocupacion.Version, "Retorno de ruta sintética AP-04."), ct);
            await rutasDemo.ConfirmarFlotaAsync(operador.Contexto, vehiculo.Id, "liberar",
                new(Guid.CreateVersion7(), ocupacion.Id, ocupacion.Version,
                    "Carga sintética recibida en depósito para AP-04."), ct);

            if (intento < cantidadIntentos)
            {
                versionEnvio = await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
                    .Select(x => x.Version).SingleAsync(ct);
                var reprogramacion = await rutasDemo.ReprogramarAsync(operador.Id, envio.Id,
                    new(versionEnvio) { SolicitudPublicaId = Guid.CreateVersion7() }, ct);
                if (reprogramacion is null)
                    throw new InvalidOperationException("No se pudo reprogramar la fixture sintética AP-04.");
            }
        }

        var versionFinalEnvio = await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
            .Select(x => x.Version).SingleAsync(ct);
        if (ordinal != 3)
        {
            var decision = await rutasDemo.IniciarDevolucionAsync(operador.Contexto, envio.Id,
                new(versionFinalEnvio, Guid.CreateVersion7(), "Tres intentos sintéticos fallidos para verificar AP-04."), ct);
            if (decision is null)
                throw new InvalidOperationException("No se pudo iniciar la devolución sintética AP-04.");

            versionFinalEnvio = await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
                .Select(x => x.Version).SingleAsync(ct);
            if (!await rutasDemo.RecibirAsync(operador.Id, envio.Id, versionFinalEnvio, ct))
                throw new InvalidOperationException("No se pudo cerrar la recepción sintética de la devolución AP-04.");
        }

        var bultosADevolver = await db.Bultos.AsNoTracking().Where(x => x.OperadorId == operador.Id
                && x.EnvioId == envio.Id && x.Custodia == EstadoCustodiaBulto.Deposito
                && (x.Resultado == ResultadoBulto.EnDevolucion || x.Resultado == ResultadoBulto.Devuelto))
            .OrderBy(x => x.Codigo).ToArrayAsync(ct);
        if (bultosADevolver.Length != 1)
            throw new InvalidOperationException("La fixture AP-04 debe dejar exactamente un bulto devuelto elegible para transferencia.");
        if (ordinal == 4)
        {
            // Historical sample: the synthetic legacy custody is Comercio, while no AP-04
            // transfer record exists to attribute a current handoff or permit an ack.
            await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
            var historico = await db.Bultos.SingleAsync(x => x.OperadorId == operador.Id
                && x.EnvioId == envio.Id && x.Id == bultosADevolver[0].Id, ct);
            if (historico.Resultado != ResultadoBulto.Devuelto || historico.Custodia != EstadoCustodiaBulto.Deposito)
                throw new InvalidOperationException("La fixture histórica AP-04 requiere un retorno recibido en depósito.");
            historico.EntregarAlComercio();
            await db.SaveChangesAsync(ct);
            return;
        }
        versionFinalEnvio = await db.Envios.AsNoTracking().Where(x => x.OperadorId == operador.Id && x.Id == envio.Id)
            .Select(x => x.Version).SingleAsync(ct);
        var transferencia = await devoluciones.TransferirAsync(operador.Contexto, envio.Id,
            new(Guid.CreateVersion7(), versionFinalEnvio,
                bultosADevolver.Select(x => new BultoDevolucionComando(x.Id, x.Version)).ToArray(),
                ConfirmarTransferenciaFisica: true), ct);
        if (transferencia is null)
            throw new InvalidOperationException("No se pudo registrar la transferencia física sintética AP-04 al comercio.");
        // The demo seed commits as Plataforma. Evaluate this deferred RLS-backed constraint while the
        // transfer's Operador context can still see the parcel lines.
        await db.Database.ExecuteSqlRawAsync(
            "SET CONSTRAINTS return_transfer_lines_required IMMEDIATE", ct);
        if (ordinal == 3)
        {
            // The temporary publication is checked eagerly above. Defer this constraint
            // again so EF can persist the restoration version and current-root pointer in
            // one transaction before PostgreSQL evaluates the published-version guard.
            await db.Database.ExecuteSqlRawAsync(
                "SET CONSTRAINTS operational_rules_current_publication_check DEFERRED", ct);
            await RestaurarReglasActivasAp04Async(operador, reglasBaseId, reglasId, DateTimeOffset.UtcNow, ct);
            await db.Database.ExecuteSqlRawAsync(
                "SET CONSTRAINTS operational_rules_current_publication_check IMMEDIATE", ct);
        }
    }

    private async Task<Guid> AsegurarReglasEntregaParcialAp04Async(OrganizacionDemo operador,
        Guid versionActualId, DateTimeOffset publicadaEnUtc, CancellationToken ct)
    {
        // Publish temporarily so the imported shipment captures a real active version.
        // The seed restores the previous behavior after the physical return is recorded.
        var fuente = await db.VersionesReglasOperativas.Include(x => x.RequisitosPrueba).Include(x => x.Motivos)
            .SingleOrDefaultAsync(x => x.OperadorId == operador.Id && x.Id == versionActualId
                && x.Estado == EstadoVersionReglasOperativas.Publicada, ct)
            ?? throw new InvalidOperationException("No se pudo leer la versión publicada de reglas del operador para AP-04.");
        var raiz = await db.ReglasOperativasOperadores.SingleAsync(x => x.OperadorId == operador.Id, ct);
        if (raiz.VersionVigenteId != fuente.Id)
            throw new InvalidOperationException("Las reglas activas del operador cambiaron durante la fixture AP-04.");
        var version = VersionReglasOperativas.CrearBorrador(operador.Id, operador.UsuarioId, fuente,
            publicadaEnUtc);
        db.VersionesReglasOperativas.Add(version);
        await db.SaveChangesAsync(ct);
        var configuracion = new ReglasOperativasConfiguracion(
            fuente.MaxIntentosEntrega,
            fuente.IntervaloEntreIntentosMinutos,
            fuente.PoliticaDevolucionAlAgotarIntentos,
            fuente.PlazoEstandarMinutos,
            fuente.PlazoUrgenteMinutos,
            fuente.PlazoValidacionPruebaMinutos,
            fuente.RequisitosPrueba.Select(x => x.Tipo).ToArray(),
            fuente.Motivos.Select(x => new MotivoNoEntregaConfiguracion(x.Codigo, x.Descripcion, x.Disponible)).ToArray())
        {
            PermiteEntregaParcial = true,
            PoliticaPendientesParcial = PoliticaPendientesParcial.DevolverComercio,
            RecargoNuevaVisitaUyu = fuente.RecargoNuevaVisitaUyu
        };
        version.ReemplazarConfiguracion(0, configuracion);
        // Persist the edited draft version before publication so its concurrency
        // counter matches the database row when the publication guard runs.
        await db.SaveChangesAsync(ct);
        version.Publicar(1, checked(raiz.UltimoNumeroPublicacion + 1), operador.UsuarioId, publicadaEnUtc);
        // Persist publication metadata before moving the root pointer: its BEFORE trigger
        // validates the referenced version from PostgreSQL, not EF's in-memory graph.
        await db.SaveChangesAsync(ct);
        raiz.Publicar(version, publicadaEnUtc);
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "SET CONSTRAINTS operational_rules_current_publication_check IMMEDIATE", ct);
        return version.Id;
    }

    private async Task RestaurarReglasActivasAp04Async(OrganizacionDemo operador, Guid versionAnteriorId,
        Guid versionParcialId, DateTimeOffset publicadaEnUtc, CancellationToken ct)
    {
        var raiz = await db.ReglasOperativasOperadores.SingleAsync(x => x.OperadorId == operador.Id, ct);
        if (raiz.VersionVigenteId != versionParcialId)
            throw new InvalidOperationException("No se puede restaurar AP-04: cambió la versión activa durante el seed.");
        var fuente = await db.VersionesReglasOperativas.Include(x => x.RequisitosPrueba).Include(x => x.Motivos)
            .SingleOrDefaultAsync(x => x.OperadorId == operador.Id && x.Id == versionAnteriorId
                && x.Estado == EstadoVersionReglasOperativas.Publicada, ct)
            ?? throw new InvalidOperationException("No se encontró la versión original de reglas para restaurar AP-04.");
        var restauracion = VersionReglasOperativas.CrearBorrador(operador.Id, operador.UsuarioId, fuente,
            publicadaEnUtc);
        db.VersionesReglasOperativas.Add(restauracion);
        await db.SaveChangesAsync(ct);
        restauracion.Publicar(0, checked(raiz.UltimoNumeroPublicacion + 1), operador.UsuarioId, publicadaEnUtc);
        // See the temporary publication above: persist the Published row first so the
        // root's immediate trigger sees the new state while validating its foreign pointer.
        await db.SaveChangesAsync(ct);
        raiz.Publicar(restauracion, publicadaEnUtc);
        await db.SaveChangesAsync(ct);
    }

    private async Task AsegurarRevisionActualCoberturaAp04Async(
        OrganizacionDemo comercio, OrganizacionDemo operador, CancellationToken ct)
    {
        const double latitud = -34.905;
        const double longitud = -56.185;
        const string modalidad = "Estandar";

        await db.ApplyAsync(RlsActorContext.From(comercio.Contexto), ct);
        var ofertas = await TarifasAdmision.OfertasAsync(db, operador.Id, latitud, longitud, ct);
        var oferta = ofertas.SingleOrDefault(x => x.Tarifa.Modalidad.ToString() == modalidad)
            ?? throw new InvalidOperationException("El operador demo no publica una oferta AP-04 inequívoca.");

        await db.ApplyAsync(RlsActorContext.From(operador.Contexto), ct);
        var zona = await db.ZonasCobertura.Include(x => x.Franjas)
            .SingleOrDefaultAsync(x => x.OperadorId == operador.Id && x.Id == oferta.Tarifa.ZonaId, ct)
            ?? throw new InvalidOperationException("La cobertura publicada para AP-04 ya no existe.");

        if (await db.ZonasCoberturaRevisiones.AsNoTracking().AnyAsync(x => x.OperadorId == operador.Id
            && x.ZonaId == zona.Id && x.NumeroRevision == oferta.RevisionZona, ct))
            return;

        // The AP-04 shipment is quoted from the published state now. Restore a missing current snapshot only;
        // a missing historical revision cannot be reconstructed from the mutable zone safely.
        if (zona.RevisionActual != oferta.RevisionZona || zona.Version != oferta.VersionZona)
            throw new InvalidOperationException("La oferta AP-04 refiere a una revisión histórica de cobertura no verificable.");

        db.ZonasCoberturaRevisiones.Add(new ZonaCoberturaRevision(operador.Id, zona.Id,
            zona.RevisionActual, zona.Version, zona.ActualizadaUtc, operador.UsuarioId,
            ZonaCoberturaSnapshotJson.Crear(zona)));
        await db.SaveChangesAsync(ct);
    }

    // Only the explicitly requested demo seed creates this synthetic service, for its new operators.
    private async Task PublicarServicioDemoAsync(OrganizacionDemo operador, CancellationToken ct,
        bool calendarioCompleto = false)
    {
        var geometria = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var poligono = geometria.CreateMultiPolygon([geometria.CreatePolygon([
            new(-56.4, -35), new(-56, -35), new(-56, -34.7), new(-56.4, -34.7), new(-56.4, -35)])]);
        var finDelDia = new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 10));
        IEnumerable<FranjaEntregaInicial> franjas = calendarioCompleto
            ? Enum.GetValues<DiaSemanaIso>()
                .Select(dia => new FranjaEntregaInicial(dia, TimeOnly.MinValue, finDelDia))
            : [new(DiaSemanaIso.Lunes, new TimeOnly(9, 0), new TimeOnly(18, 0))];
        var zona = ZonaCobertura.Crear(operador.Id, "Cobertura demo Montevideo", poligono, id: operador.Id,
            franjasIniciales: franjas);
        await zonasCobertura.GuardarNuevaAsync(zona, operador.UsuarioId, ct);
        var versionZona = zona.Version;
        zona.Activar(versionZona);
        await zonasCobertura.GuardarCambioAsync(zona, versionZona, operador.UsuarioId, ct);
        var tarifario = TarifarioOperador.Crear(operador.Id);
        db.TarifariosOperador.Add(tarifario);
        await db.SaveChangesAsync(ct);
        var version = VersionTarifario.CrearBorrador(operador.Id, operador.UsuarioId);
        db.VersionesTarifario.Add(version);
        await db.SaveChangesAsync(ct);
        version.ReemplazarConfiguracion(0, [new(operador.Id, ModalidadTarifaria.Estandar, 100m, 10m, 200m, [])]);
        await db.SaveChangesAsync(ct);
        version.Publicar(version.VersionConcurrencia, 1, operador.UsuarioId, new HashSet<Guid> { zona.Id });
        await db.SaveChangesAsync(ct);
        tarifario.Publicar(version);
        await db.SaveChangesAsync(ct);
    }

    private static DateTimeOffset AFechaUtc(DateOnly fecha, TimeOnly hora)
    {
        var local = fecha.ToDateTime(hora, DateTimeKind.Unspecified);
        return new(TimeZoneInfo.ConvertTimeToUtc(local, ZonaHoraria), TimeSpan.Zero);
    }

    private static string CrearPasswordAleatoria()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    private sealed record OrganizacionDemo(Guid Id, string Nombre, string Email, Guid UsuarioId,
        Guid AccesoId, Guid RolId, ContextoUsuario Contexto);
    private sealed record EnvioDemo(Envio Envio, Destinatario Destinatario, Bulto Bulto, double Latitud, double Longitud);

    private sealed class UsuarioActualDemo(ContextoUsuario contexto) : NexoRuta.Application.Administracion.Interfaces.IUsuarioActual
    {
        public Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(contexto);
        }
    }

    private sealed class RelojDemo(DateTimeOffset instante) : TimeProvider
    {
        private DateTimeOffset actual = instante;
        public override DateTimeOffset GetUtcNow() => actual;
        public void AvanzarA(DateTimeOffset nuevoInstante) => actual = nuevoInstante.ToUniversalTime();
    }
}

public sealed record EstadoDatosDemo(bool Sembrada, DateTimeOffset? SembradoEnUtc,
    int Operadores, int Comercios, int CuentasOperador, int CuentasComercio,
    int Envios, int Rutas, int Operaciones)
{
    public static EstadoDatosDemo Vacio { get; } = new(false, null, 0, 0, 0, 0, 0, 0, 0);

    public static EstadoDatosDemo Desde(EjecucionSeedDemo ejecucion) => new(true, ejecucion.SembradoEnUtc,
        ejecucion.Operadores, ejecucion.Comercios, ejecucion.CuentasOperador, ejecucion.CuentasComercio,
        ejecucion.Envios, ejecucion.Rutas, ejecucion.Operaciones);
}

public sealed record CredencialFixtureCarga(string Email, string Password);
public sealed record CuentaFixtureCargaSembrada(string Email, Guid JornadaId,
    IReadOnlyList<OperacionRepartoDatos> Operaciones);

