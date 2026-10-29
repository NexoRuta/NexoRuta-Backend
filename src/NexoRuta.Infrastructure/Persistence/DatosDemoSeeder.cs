using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Repositories;
using NexoRuta.Application.Reparto.Dtos;
using NexoRuta.Application.Reparto.Repositories;
using NexoRuta.Application.Reparto.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.ReglasOperativas;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Reparto;

namespace NexoRuta.Infrastructure.Persistence;

public sealed class DatosDemoSeeder(
    NexoRutaDbContext db,
    IPlataformaRepository plataforma,
    IRepartidoresRepository repartidores,
    IConfiguracionRepartoRepository configuracionReparto,
    IRutasRepository rutas,
    IConfiguration configuration)
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
        if (anterior is not null) return (true, EstadoDatosDemo.Desde(anterior));

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
            await PublicarReglasAsync(operador, fechaReglas, ct);
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
                    if (!await rutas.RecibirAsync(operador.Id, envio.Envio.Id, envio.Envio.Version, ct))
                        throw new InvalidOperationException("No se pudo recibir un envío demo en depósito.");

                var paradas = enviosDia.Select((envio, index) => new ParadaPlanificadaDatos(
                    envio.Envio.Id, horaInicio.AddHours(1).AddMinutes(index * 45))).ToArray();
                var hoja = await rutas.PlanificarAsync(operador.Id,
                    new(repartidor.AccesoId, vehiculo.Id, fechaRuta, paradas), ct);
                var despachada = await rutas.DespacharAsync(operador.Id, hoja.Id, hoja.Version, ct)
                    ?? throw new InvalidOperationException("No se pudo despachar una ruta demo.");

                var relojDemo = new RelojDemo(horaInicio);
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
                cantidadRutas++;
            }
        }

        await db.ApplyAsync(RlsActorContext.From(actor), ct);
        var resultado = new EjecucionSeedDemo(DateTimeOffset.UtcNow, operadores.Count, comercios.Count,
            operadores.Count, comercios.Count, cantidadEnvios, cantidadRutas, cantidadOperaciones);
        db.EjecucionesSeedDemo.Add(resultado);
        await db.SaveChangesAsync(ct);
        return (false, EstadoDatosDemo.Desde(resultado));
    }

    private async Task<OrganizacionDemo> CrearOrganizacionAsync(TipoAccesoUsuario tipo, string nombre,
        string email, CancellationToken ct)
    {
        var alta = await plataforma.CrearAsync(tipo, nombre, email, CrearPasswordAleatoria(), ct);
        var operadorId = tipo == TipoAccesoUsuario.Operador ? alta.Organizacion.Id : (Guid?)null;
        var comercioId = tipo == TipoAccesoUsuario.Comercio ? alta.Organizacion.Id : (Guid?)null;
        var contexto = new ContextoUsuario(alta.AccesoId, alta.UsuarioId, operadorId, comercioId, email,
            operadorId is null ? null : nombre, comercioId is null ? null : nombre, alta.RolId, "administrador");
        return new(alta.Organizacion.Id, nombre, email, alta.UsuarioId, alta.AccesoId, alta.RolId, contexto);
    }

    private async Task PublicarReglasAsync(OrganizacionDemo operador, DateTimeOffset publicadoEn, CancellationToken ct)
    {
        var raiz = ReglasOperativasOperador.Crear(operador.Id, publicadoEn);
        db.ReglasOperativasOperadores.Add(raiz);
        await db.SaveChangesAsync(ct);

        var version = VersionReglasOperativas.CrearBorrador(operador.Id, operador.UsuarioId, ahoraUtc: publicadoEn);
        version.ReemplazarConfiguracion(0,
            new(3, 120, PoliticaDevolucionAlAgotarIntentos.DecisionOperativa, 1440, 360,
                [TipoPruebaEntrega.NombreReceptor],
                [new("DESTINATARIO_AUSENTE", "Destinatario ausente", true),
                 new("DIRECCION_INCORRECTA", "Dirección incompleta", true)]));
        version.Publicar(1, 1, operador.UsuarioId, publicadoEn);
        db.VersionesReglasOperativas.Add(version);
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
        direccion.ConfirmarUbicacion(latitud, longitud, comercio.UsuarioId, creadoEn, version: 0);
        var envio = new Envio(operador.Id, comercio.Id, comercio.UsuarioId, destinatario.Id, direccion.Id,
            versionReglasId, creadoEn);
        var bulto = new Bulto(operador.Id, envio.Id, $"NR-{creadoEn:yyMMdd}-{ordinal + 1:0000}",
            650 + ordinal % 8 * 420, 28 + ordinal % 4 * 5, 18 + ordinal % 3 * 4, 10 + ordinal % 4 * 3);
        await db.SetShipmentAsync(operador.Id, envio.Id, destinatario.Id, direccion.Id, bulto.Id, ct);
        db.AddRange(destinatario, direccion, envio, bulto);
        await db.SaveChangesAsync(ct);
        return new(envio, destinatario, bulto, latitud, longitud);
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
