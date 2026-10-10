using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Application.Administracion.Context;
using NexoRuta.Application.Administracion.Exceptions;
using NexoRuta.Application.Administracion.Interfaces;
using NexoRuta.Application.Envios.Commands;
using NexoRuta.Application.Envios.Results;
using NexoRuta.Application.Envios.UseCases;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Administracion;
using NexoRuta.Infrastructure.Administracion.Repositories;
using NexoRuta.Infrastructure.Envios.Repositories;
using NexoRuta.Infrastructure.Persistence;
using Npgsql;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class EnvioPostgresRoundTripTests : IAsyncLifetime
{
    private DbContextOptions<NexoRutaDbContext> options = null!;

    public async Task InitializeAsync()
    {
        var source = Environment.GetEnvironmentVariable("NEXORUTA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(source))
            source = "Host=127.0.0.1;Port=5432;Database=nexoruta;Username=nexoruta;Password=nexoruta_dev";
        var connection = new NpgsqlConnectionStringBuilder(source) { Database = $"nexoruta_test_{Guid.CreateVersion7():N}" };
        options = new DbContextOptionsBuilder<NexoRutaDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var db = new NexoRutaDbContext(options);
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = new NexoRutaDbContext(options);
        await db.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Cuentas_InicializaUnDuenoDelComercioYUnaCuentaDelOperadorSinDuplicarlos()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var seeder = new DatosInicialesSeeder(db, inicial);
        await seeder.SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        await seeder.SeedAsync();

        Assert.True(comercio.EsPropietario);
        Assert.Null(comercio.OperadorId);
        Assert.NotNull(comercio.ComercioId);
        Assert.Null(comercio.OperadorNombre);
        Assert.Null(operador.ComercioId);
        Assert.False(operador.EsPropietario);
        Assert.Equal(2, await db.Usuarios.CountAsync());
        Assert.Equal(2, await db.AccesosUsuario.CountAsync());
        Assert.Equal(comercio, await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio));
        Assert.Equal(operador, await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador));
    }

    [Fact]
    public async Task Alta_ElDuenoEligeOperadorPorEnvioConservandoSuCuentaYElBulto()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var cuenta = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var cuentas = new EfAccesosUsuarioRepository(db);
        var primero = Assert.Single(await cuentas.ListarOperadoresAsync());
        var segundo = new Operador("Otro operador disponible");
        db.Operadores.Add(segundo);
        await db.SaveChangesAsync();
        Assert.Equal(2, (await cuentas.ListarOperadoresAsync()).Count);
        Assert.Single(await cuentas.ListarAsync(TipoAccesoUsuario.Comercio));

        var uno = await Crear(db, cuenta.AccesoId, primero.OperadorId, "PRIMERO");
        var dos = await Crear(db, cuenta.AccesoId, segundo.Id, "SEGUNDO");

        Assert.Equal(cuenta.UsuarioId, uno.Origen.CreadoPorUsuarioId);
        Assert.Equal(cuenta.UsuarioId, dos.Origen.CreadoPorUsuarioId);
        Assert.Equal(primero.OperadorId, uno.Origen.OperadorId);
        Assert.Equal(segundo.Id, dos.Origen.OperadorId);
        Assert.Equal(cuenta.ComercioId, uno.Origen.ComercioId);
        Assert.Equal(cuenta.ComercioId, dos.Origen.ComercioId);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        Assert.Equal(cuenta, await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio));

        await using var consulta = new NexoRutaDbContext(options);
        var listados = await Listar(consulta, cuenta.AccesoId);
        Assert.Equal(2, listados.Count);
        var persistido = Assert.Single(listados, x => x.Id == dos.Id);
        Assert.Equal(cuenta.ComercioId, persistido.Origen.ComercioId);
        Assert.Equal("Destinatario de prueba", persistido.DestinatarioNombre);
        Assert.Equal("Av. Rivera 123", persistido.Direccion);
        var bulto = Assert.Single(persistido.Bultos);
        Assert.Equal("SEGUNDO", bulto.Codigo);
        Assert.Equal(1750m, bulto.PesoGramos);
        Assert.Equal(32m, bulto.LargoCentimetros);
        Assert.Equal(21m, bulto.AnchoCentimetros);
        Assert.Equal(11m, bulto.AltoCentimetros);
    }

    [Fact]
    public async Task Alta_RechazaOperadorInexistenteYCuentaDeOperadorSinGuardar()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        await Assert.ThrowsAsync<OperadorNoDisponibleException>(() => Crear(db, comercio.AccesoId, Guid.CreateVersion7(), "DENEGADO"));
        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() => Crear(db, operador.AccesoId, operador.OperadorId!.Value, "DENEGADO"));
        Assert.Empty(await db.Envios.ToListAsync());
        Assert.Empty(await db.Bultos.ToListAsync());
    }

    [Fact]
    public async Task Consulta_ElComercioVeSusEnviosConDistintosOperadoresYBackofficeSoloSuOperador()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        var segundoOperador = new Operador("Segundo operador");
        var otraEmpresa = new Comercio("Otro comercio");
        var otroUsuario = new Usuario("otro@comercio.local");
        var otroAcceso = new AccesoUsuario(otroUsuario.Id, null, otraEmpresa.Id, true);
        db.AddRange(segundoOperador, otraEmpresa, otroUsuario, otroAcceso);
        await db.SaveChangesAsync();

        var propioUno = await Crear(db, comercio.AccesoId, operador.OperadorId!.Value, "PROPIO-UNO");
        var propioDos = await Crear(db, comercio.AccesoId, segundoOperador.Id, "PROPIO-DOS");
        var ajeno = await Crear(db, otroAcceso.Id, operador.OperadorId.Value, "AJENO");
        var propios = await Listar(db, comercio.AccesoId);
        Assert.Equal(2, propios.Count);
        Assert.Contains(propios, x => x.Id == propioUno.Id);
        Assert.Contains(propios, x => x.Id == propioDos.Id);
        Assert.DoesNotContain(propios, x => x.Id == ajeno.Id);
        var delOperador = await Listar(db, operador.AccesoId);
        Assert.Equal(2, delOperador.Count);
        Assert.Contains(delOperador, x => x.Id == propioUno.Id);
        Assert.Contains(delOperador, x => x.Id == ajeno.Id);
        Assert.DoesNotContain(delOperador, x => x.Id == propioDos.Id);
    }

    [Fact]
    public async Task Inicializacion_ReparaLaCuentaFaltanteYConservaLosNombresEditados()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var usuario = new Usuario(inicial.UsuarioEmail);
        db.Usuarios.Add(usuario);
        await db.SaveChangesAsync();
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        Assert.Equal(usuario.Id, comercio.UsuarioId);
        db.AccesosUsuario.Remove(await db.AccesosUsuario.SingleAsync(x => x.Id == comercio.AccesoId));
        await db.SaveChangesAsync();
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var reparada = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        Assert.Equal(comercio.ComercioId, reparada.ComercioId);
        Assert.Equal(usuario.Id, reparada.UsuarioId);
        db.Entry(await db.Comercios.SingleAsync()).Property(x => x.Nombre).CurrentValue = "Comercio renombrado";
        db.Entry(await db.Operadores.SingleAsync()).Property(x => x.Nombre).CurrentValue = "Operador renombrado";
        await db.SaveChangesAsync();
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        Assert.Equal("Comercio renombrado", (await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio)).ComercioNombre);
        Assert.Equal("Operador renombrado", (await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador)).OperadorNombre);
        Assert.Equal(1, await db.Comercios.CountAsync());
        Assert.Equal(1, await db.Operadores.CountAsync());
        Assert.Equal(2, await db.AccesosUsuario.CountAsync());
    }

    [Fact]
    public async Task Inicializacion_NoConvierteDatosHistoricosPorSusNombres()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var usuario = new Usuario("demo@nexoruta.local");
        var comercio = new Comercio("Comercio Demo");
        var operador = new Operador("Operador Demo");
        var acceso = new AccesoUsuario(usuario.Id, null, comercio.Id, true);
        db.AddRange(usuario, comercio, operador, acceso);
        await db.SaveChangesAsync();

        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        db.ChangeTracker.Clear();

        Assert.Equal("demo@nexoruta.local", (await db.Usuarios.SingleAsync(x => x.Id == usuario.Id)).Email);
        Assert.Equal("Comercio Demo", (await db.Comercios.SingleAsync(x => x.Id == comercio.Id)).Nombre);
        Assert.Equal("Operador Demo", (await db.Operadores.SingleAsync(x => x.Id == operador.Id)).Nombre);
        Assert.Equal(comercio.Id, (await db.AccesosUsuario.SingleAsync(x => x.Id == acceso.Id)).ComercioId);
        var configurada = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        Assert.NotEqual(usuario.Id, configurada.UsuarioId);
        Assert.NotEqual(comercio.Id, configurada.ComercioId);
        Assert.NotEqual(operador.Id, (await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador)).OperadorId);
    }

    [Fact]
    public async Task Cuenta_DeOperadorNoPuedeTenerOtraPertenenciaDeComercio()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        db.AccesosUsuario.Add(new AccesoUsuario(operador.UsuarioId, null, comercio.ComercioId!.Value));
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
        Assert.Null(await new EfAccesosUsuarioRepository(db).ObtenerAsync(Guid.CreateVersion7()));
    }

    [Fact]
    public async Task Migracion_ConservaUsuarioEnvioYCuentaAlSepararLaEleccionDeOperador()
    {
        const string anterior = "20261008163520_OperatorUserAccess";
        const string actual = "20261008180729_AccountOwnerAndShipmentOperator";
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(anterior);
        var opUno = new Operador(inicial.OperadorNombre);
        var opDos = new Operador("Otro operador anterior");
        var comercio = new Comercio(inicial.ComercioNombre);
        var usuario = new Usuario(inicial.UsuarioEmail);
        var vinculoUno = Guid.CreateVersion7();
        var vinculoDos = Guid.CreateVersion7();
        db.AddRange(opUno, opDos, comercio, usuario);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"OperadoresComercios\" (\"Id\", \"OperadorId\", \"ComercioId\") VALUES ({vinculoUno}, {opUno.Id}, {comercio.Id}), ({vinculoDos}, {opDos.Id}, {comercio.Id})");
        // ponytail: migration retains the lowest UUID; same-millisecond UUIDv7 calls are not ordered.
        var cuentaAnterior = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        var segundoAcceso = Guid.Parse("018f0000-0000-7000-8000-000000000002");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"OperadorId\", \"OperadorComercioId\") VALUES ({cuentaAnterior}, {usuario.Id}, {opUno.Id}, {vinculoUno}), ({segundoAcceso}, {usuario.Id}, {opDos.Id}, {vinculoDos})");
        var destinatario = new Destinatario(opUno.Id, "Destinatario anterior");
        var direccion = new Direccion(opUno.Id, "Direccion anterior");
        var envio = new Envio(opUno.Id, comercio.Id, usuario.Id, destinatario.Id, direccion.Id);
        var bulto = new Bulto(opUno.Id, envio.Id, "ANTERIOR", 1750m, 32m, 21m, 11m);
        db.AddRange(destinatario, direccion);
        await db.SaveChangesAsync();
        await InsertarEnvioAnterior(db, envio, vinculoUno);
        db.Bultos.Add(bulto);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(actual);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var cuenta = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        Assert.Equal(cuentaAnterior, cuenta.AccesoId);
        Assert.Equal(usuario.Id, cuenta.UsuarioId);
        Assert.Equal(comercio.Id, cuenta.ComercioId);
        Assert.Null(cuenta.OperadorId);
        Assert.True(cuenta.EsPropietario);
        Assert.Single(await new EfAccesosUsuarioRepository(db).ListarAsync(TipoAccesoUsuario.Comercio));
        Assert.Equal(envio.Id, await db.Database.SqlQueryRaw<Guid>("SELECT \"Id\" AS \"Value\" FROM \"Envios\"").SingleAsync());

        await migrator.MigrateAsync(anterior);
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AccesosUsuario\"").SingleAsync());
        await migrator.MigrateAsync();
        Assert.Equal(usuario.Id, (await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio)).UsuarioId);
        var persistido = Assert.Single(await Listar(db, cuenta.AccesoId));
        Assert.Equal(envio.Id, persistido.Id);
        Assert.Equal(comercio.Id, persistido.Origen.ComercioId);
        Assert.Equal(bulto.Codigo, Assert.Single(persistido.Bultos).Codigo);
    }

    [Fact]
    public async Task Migracion_EliminaElVinculoYConservaLosEnviosAlMigrarYRevertir()
    {
        const string anterior = "20261008180729_AccountOwnerAndShipmentOperator";
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(anterior);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var cuenta = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        var vinculo = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"OperadoresComercios\" (\"Id\", \"OperadorId\", \"ComercioId\") VALUES ({vinculo}, {operador.OperadorId!.Value}, {cuenta.ComercioId!.Value})");
        var destinatario = new Destinatario(operador.OperadorId.Value, "Destinatario anterior");
        var direccion = new Direccion(operador.OperadorId.Value, "Dirección anterior");
        db.AddRange(destinatario, direccion);
        await db.SaveChangesAsync();
        var envio = new Envio(operador.OperadorId.Value, cuenta.ComercioId.Value, cuenta.UsuarioId, destinatario.Id, direccion.Id);
        await InsertarEnvioAnterior(db, envio, vinculo);
        var bulto = new Bulto(envio.OperadorId, envio.Id, "ANTERIOR", 1750m, 32m, 21m, 11m);
        db.Bultos.Add(bulto);
        await db.SaveChangesAsync();

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('\"OperadoresComercios\"') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Equal(cuenta, await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio));
        var persistido = Assert.Single(await Listar(db, cuenta.AccesoId));
        Assert.Equal(envio.Id, persistido.Id);
        Assert.Equal(cuenta.ComercioId, persistido.Origen.ComercioId);
        Assert.Equal(cuenta.UsuarioId, persistido.Origen.CreadoPorUsuarioId);
        Assert.Equal(bulto.Codigo, Assert.Single(persistido.Bultos).Codigo);

        var nuevoOperador = new Operador("Operador nuevo sin vínculo previo");
        db.Operadores.Add(nuevoOperador);
        await db.SaveChangesAsync();
        var nuevo = await Crear(db, cuenta.AccesoId, nuevoOperador.Id, "NUEVO");
        var repetido = await Crear(db, cuenta.AccesoId, nuevoOperador.Id, "REPETIDO");

        await migrator.MigrateAsync(anterior);
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"OperadoresComercios\"").SingleAsync());
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"Envios\" e JOIN \"OperadoresComercios\" r ON e.\"OperadorComercioId\" = r.\"Id\" AND e.\"OperadorId\" = r.\"OperadorId\"").SingleAsync());
        await migrator.MigrateAsync();
        var envios = await Listar(db, cuenta.AccesoId);
        Assert.Equal(3, envios.Count);
        Assert.Contains(envios, x => x.Id == envio.Id && x.Origen.ComercioId == cuenta.ComercioId);
        Assert.Contains(envios, x => x.Id == nuevo.Id && x.Origen.ComercioId == cuenta.ComercioId);
        Assert.Contains(envios, x => x.Id == repetido.Id && x.Origen.ComercioId == cuenta.ComercioId);
        Assert.Equal(3, await db.Bultos.CountAsync());
        Assert.True(await db.Bultos.AnyAsync(x => x.Id == bulto.Id && x.EnvioId == envio.Id));
    }

    [Fact]
    public async Task Guardar_FkDeTenantInvalidaRevierteTodoElEnvio()
    {
        await using var db = new NexoRutaDbContext(options);
        var inicial = Datos();
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        var destinatario = new Destinatario(operador.OperadorId!.Value, "Ana");
        var direccion = new Direccion(operador.OperadorId.Value, "Rivera 123");
        var envio = new Envio(operador.OperadorId.Value, comercio.ComercioId!.Value,
            comercio.UsuarioId, destinatario.Id, direccion.Id);
        var bulto = new Bulto(Guid.CreateVersion7(), envio.Id, "FK-INVALIDA", 100m, 10m, 20m, 30m);

        var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
            new EfEnviosRepository(db).GuardarAsync(envio, destinatario, direccion, bulto));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);

        await using var consulta = new NexoRutaDbContext(options);
        Assert.Empty(await consulta.Envios.ToListAsync());
        Assert.Empty(await consulta.Bultos.ToListAsync());
        Assert.Empty(await consulta.Destinatarios.ToListAsync());
        Assert.Empty(await consulta.Direcciones.ToListAsync());
    }

    private static Task<int> InsertarEnvioAnterior(NexoRutaDbContext db, Envio envio, Guid vinculoId)
        => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Envios\" (\"Id\", \"OperadorId\", \"OperadorComercioId\", \"CreadoPorUsuarioId\", \"DestinatarioId\", \"DireccionId\", \"Estado\") VALUES ({envio.Id}, {envio.OperadorId}, {vinculoId}, {envio.CreadoPorUsuarioId}, {envio.DestinatarioId}, {envio.DireccionId}, 'Admitido')");

    private static AccesoInicial Datos()
    {
        var id = Guid.CreateVersion7().ToString("N");
        return new AccesoInicial($"dueno-{id}@nexoruta.local", $"Operador {id}", $"Comercio {id}", $"operador-{id}@nexoruta.local");
    }

    private static async Task<ContextoUsuario> Cuenta(NexoRutaDbContext db, string email, TipoAccesoUsuario tipo)
        => Assert.Single(await new EfAccesosUsuarioRepository(db).ListarAsync(tipo), x => x.UsuarioEmail == email);

    private static Task<EnvioCreado> Crear(NexoRutaDbContext db, Guid accesoId, Guid operadorId, string codigo)
        => new CrearEnvioUseCase(new EfEnviosRepository(db), new UsuarioSeleccionado(db, accesoId), new EfAccesosUsuarioRepository(db))
            .EjecutarAsync(new CrearEnvioCommand(
                OperadorId: operadorId,
                DestinatarioNombre: "Destinatario de prueba",
                Direccion: "Av. Rivera 123",
                CodigoBulto: codigo,
                PesoGramos: 1750m,
                LargoCentimetros: 32m,
                AnchoCentimetros: 21m,
                AltoCentimetros: 11m));

    private static Task<IReadOnlyList<EnvioDetalle>> Listar(NexoRutaDbContext db, Guid accesoId)
        => new ListarEnviosUseCase(new EfEnviosRepository(db), new UsuarioSeleccionado(db, accesoId)).EjecutarAsync();

    private sealed class UsuarioSeleccionado(NexoRutaDbContext db, Guid accesoId) : IUsuarioActual
    {
        public async Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => await new EfAccesosUsuarioRepository(db).ObtenerAsync(accesoId, cancellationToken)
                ?? throw new AccesoActualNoDisponibleException();
    }
}
