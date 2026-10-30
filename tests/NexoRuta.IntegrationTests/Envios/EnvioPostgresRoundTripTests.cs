using NexoRuta.IntegrationTests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Application.Administracion.Dtos;
using NexoRuta.Application.Administracion.Repositories;
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
using System.Data.Common;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class EnvioPostgresRoundTripTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private DbContextOptions<NexoRutaDbContext> options = null!;
    private string connectionString = null!;

    public async Task InitializeAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        connectionString = database.ConnectionString;
        options = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNexoRutaPostgres(database.ConnectionString).Options;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SuspensionConfirmedBeforeShipmentLockPreventsNewShipmentAndOutboxRows()
    {
        var accessData = Datos();
        await using (var seed = new NexoRutaDbContext(options))
            await AuthenticationTestData.Seeder(seed, accessData).SeedAsync();
        Guid commerceId;
        Guid operatorId;
        Guid userId;
        Guid commerceAccessId;
        await using (var seed = new NexoRutaDbContext(options))
        {
            commerceId = await seed.Comercios.Select(x => x.Id).SingleAsync();
            operatorId = await seed.Operadores.Select(x => x.Id).SingleAsync();
            commerceAccessId = await seed.AccesosUsuario.Where(x => x.ComercioId != null).Select(x => x.Id).SingleAsync();
            userId = await seed.AccesosUsuario.Where(x => x.ComercioId != null).Select(x => x.UsuarioId).SingleAsync();
        }

        await using var suspension = new NexoRutaDbContext(options);
        await using var transaction = await suspension.Database.BeginTransactionAsync();
        var commerce = await suspension.Comercios.SingleAsync(x => x.Id == commerceId);
        commerce.CambiarEstado(EstadoOrganizacion.Suspendida);
        await suspension.SaveChangesAsync();

        var lockObserver = new ShipmentLockObserver();
        var optionsWithObserver = new DbContextOptionsBuilder<NexoRutaDbContext>()
            .UseNexoRutaPostgres(connectionString)
            .AddInterceptors(lockObserver)
            .Options;
        await using var shipmentDb = new NexoRutaDbContext(optionsWithObserver);
        var context = await new EfAccesosUsuarioRepository(shipmentDb).ObtenerAsync(commerceAccessId)
            ?? throw new InvalidOperationException("The seeded commerce access is missing.");
        await using var shipmentTransaction = await shipmentDb.Database.BeginTransactionAsync();
        await shipmentDb.ApplyAsync(RlsActorContext.From(context));
        var recipient = new Destinatario(operatorId, "Test recipient", "destinatario@example.test");
        var address = new Direccion(operatorId, "Test address");
        var shipment = new Envio(operatorId, commerceId, userId, recipient.Id, address.Id, operatorId);
        var parcel = new Bulto(operatorId, shipment.Id, "RACE-1", 100m, 10m, 10m, 10m);
        var save = new EfEnviosRepository(shipmentDb).GuardarAsync(shipment, recipient, address, parcel);
        await lockObserver.LockStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(save.IsCompleted);

        await transaction.CommitAsync();
        await Assert.ThrowsAsync<OrganizacionSuspendidaException>(() => save);
        await using var verify = new NexoRutaDbContext(options);
        Assert.Empty(await verify.Envios.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Empty(await verify.Destinatarios.ToListAsync());
        Assert.Empty(await verify.Direcciones.ToListAsync());
    }

    private sealed class ShipmentLockObserver : DbCommandInterceptor
    {
        public TaskCompletionSource LockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                LockStarted.TrySetResult();
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Repositorios_ConsultanEmpresasSinUsuariosNiEnvios()
    {
        using var servicios = Servicios();
        using var scope = servicios.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoRutaDbContext>();
        var operadorAlfa = new Operador("Alfa repartos");
        var operadorBeta = new Operador("Beta repartos");
        var comercioAlfa = new Comercio("Alfa ventas");
        var comercioBeta = new Comercio("Beta ventas");
        db.AddRange(operadorBeta, operadorAlfa, comercioBeta, comercioAlfa);
        await db.SaveChangesAsync();

        var operadores = scope.ServiceProvider.GetRequiredService<IOperadoresRepository>();
        var comercios = scope.ServiceProvider.GetRequiredService<IComerciosRepository>();
        Assert.Equal(new[] {
            new OperadorDisponible(operadorAlfa.Id, operadorAlfa.Nombre),
            new OperadorDisponible(operadorBeta.Id, operadorBeta.Nombre)
        }, (await operadores.ListarAsync()).ToArray());
        Assert.Equal(new[] {
            new ComercioResumen(comercioAlfa.Id, comercioAlfa.Nombre),
            new ComercioResumen(comercioBeta.Id, comercioBeta.Nombre)
        }, (await comercios.ListarAsync()).ToArray());
        Assert.Equal(new OperadorDisponible(operadorBeta.Id, operadorBeta.Nombre), await operadores.ObtenerAsync(operadorBeta.Id));
        Assert.Equal(new ComercioResumen(comercioBeta.Id, comercioBeta.Nombre), await comercios.ObtenerAsync(comercioBeta.Id));
        Assert.Null(await operadores.ObtenerAsync(Guid.CreateVersion7()));
        Assert.Null(await comercios.ObtenerAsync(Guid.CreateVersion7()));
        var accesos = scope.ServiceProvider.GetRequiredService<IAccesosUsuarioRepository>();
        Assert.Empty(await accesos.ListarAsync(TipoAccesoUsuario.Comercio));
        Assert.Empty(await accesos.ListarAsync(TipoAccesoUsuario.Operador));
        Assert.Empty(await db.Usuarios.ToListAsync());
        Assert.Empty(await db.Envios.ToListAsync());
    }

    [Fact]
    public async Task Repositorios_RespetanLaCancelacionEnLasConsultasDeEmpresas()
    {
        using var servicios = Servicios();
        using var scope = servicios.CreateScope();
        var operadores = scope.ServiceProvider.GetRequiredService<IOperadoresRepository>();
        var comercios = scope.ServiceProvider.GetRequiredService<IComerciosRepository>();
        using var cancelacion = new CancellationTokenSource();
        cancelacion.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operadores.ListarAsync(cancelacion.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operadores.ObtenerAsync(Guid.CreateVersion7(), cancelacion.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comercios.ListarAsync(cancelacion.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => comercios.ObtenerAsync(Guid.CreateVersion7(), cancelacion.Token));
    }

    [Fact]
    public async Task Cuentas_InicializaUnDuenoDelComercioYUnaCuentaDelOperadorSinDuplicarlos()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var seeder = AuthenticationTestData.Seeder(db, inicial);
        await seeder.SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        await seeder.SeedAsync();

        Assert.Equal("propietario_comercio", comercio.RolCodigo);
        Assert.NotEqual(Guid.Empty, comercio.RolId);
        Assert.Null(comercio.OperadorId);
        Assert.NotNull(comercio.ComercioId);
        Assert.Null(comercio.OperadorNombre);
        Assert.Null(operador.ComercioId);
        Assert.Equal("administrador_operador", operador.RolCodigo);
        Assert.NotEqual(Guid.Empty, operador.RolId);
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
        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
        var cuenta = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var cuentas = new EfAccesosUsuarioRepository(db);
        var operadores = new EfOperadoresRepository(db);
        var primero = Assert.Single(await operadores.ListarAsync());
        var segundo = new Operador("Otro operador disponible");
        db.Operadores.Add(segundo);
        await db.SaveChangesAsync();
        Assert.Equal(2, (await operadores.ListarAsync()).Count);
        Assert.Single(await cuentas.ListarAsync(TipoAccesoUsuario.Comercio));

        var uno = await Crear(db, cuenta.AccesoId, primero.OperadorId, "PRIMERO");
        var dos = await Crear(db, cuenta.AccesoId, segundo.Id, "SEGUNDO");

        Assert.Equal(cuenta.UsuarioId, uno.Origen.CreadoPorUsuarioId);
        Assert.Equal(cuenta.UsuarioId, dos.Origen.CreadoPorUsuarioId);
        Assert.Equal(primero.OperadorId, uno.Origen.OperadorId);
        Assert.Equal(segundo.Id, dos.Origen.OperadorId);
        Assert.Equal(cuenta.ComercioId, uno.Origen.ComercioId);
        Assert.Equal(cuenta.ComercioId, dos.Origen.ComercioId);
        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
        Assert.Equal(cuenta, await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio));

        await using var consulta = new NexoRutaDbContext(options);
        var contexto = await new EfAccesosUsuarioRepository(consulta).ObtenerAsync(cuenta.AccesoId)
            ?? throw new InvalidOperationException("The selected commerce access is missing.");
        await using (var transaccion = await consulta.BeginOrJoinAsync())
        {
            await consulta.ApplyAsync(RlsActorContext.From(contexto));
            await consulta.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "IdentidadesEnvioImportado"
                    ("ComercioId", "ReferenciaExterna", "EnvioId", "OperadorId", "VersionHuella", "Huella")
                VALUES ({cuenta.ComercioId!.Value}, {"EXT-POSTGRES-1"}, {dos.Id}, {segundo.Id}, {(short)1}, {new byte[32]})
                """);
            await transaccion.CommitAsync();
        }
        var listados = await Listar(consulta, cuenta.AccesoId);
        Assert.Equal(2, listados.Count);
        var persistido = Assert.Single(listados, x => x.Id == dos.Id);
        Assert.Equal(cuenta.ComercioId, persistido.Origen.ComercioId);
        Assert.Equal("Destinatario de prueba", persistido.DestinatarioNombre);
        Assert.Equal("destinatario@example.test", persistido.DestinatarioEmail);
        Assert.Equal("EXT-POSTGRES-1", persistido.ReferenciaExterna);
        Assert.Equal("Av. Rivera 123", persistido.Direccion);
        var bulto = Assert.Single(persistido.Bultos);
        Assert.Equal("SEGUNDO", bulto.Codigo);
        Assert.Equal(1750m, bulto.PesoGramos);
        Assert.Equal(32m, bulto.LargoCentimetros);
        Assert.Equal(21m, bulto.AnchoCentimetros);
        Assert.Equal(11m, bulto.AltoCentimetros);
        Assert.False(persistido.DestinoConfirmado);

        var envioDb = await consulta.Envios.SingleAsync(e => e.Id == dos.Id);
        var direccionDb = await consulta.Direcciones.SingleAsync(d => d.Id == envioDb.DireccionId);
        direccionDb.ConfirmarUbicacion(-34.9, -56.2, cuenta.UsuarioId, DateTimeOffset.UtcNow, direccionDb.Version);
        await consulta.SaveChangesAsync();
        var confirmado = Assert.Single(await Listar(consulta, cuenta.AccesoId), e => e.Id == dos.Id);
        Assert.True(confirmado.DestinoConfirmado);
    }

    [Fact]
    public async Task Alta_RechazaOperadorInexistenteYCuentaDeOperadorSinGuardar()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        await Assert.ThrowsAsync<OperadorNoEncontradoException>(() => Crear(db, comercio.AccesoId, Guid.CreateVersion7(), "DENEGADO"));
        await Assert.ThrowsAsync<AccesoNoPermitidoException>(() => Crear(db, operador.AccesoId, operador.OperadorId!.Value, "DENEGADO"));
        Assert.Empty(await db.Envios.ToListAsync());
        Assert.Empty(await db.Bultos.ToListAsync());
    }

    [Fact]
    public async Task Consulta_ElComercioVeSusEnviosConDistintosOperadoresYBackofficeSoloSuOperador()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        var segundoOperador = new Operador("Segundo operador");
        var otraEmpresa = new Comercio("Otro comercio");
        var otroUsuario = new Usuario("otro@comercio.local", AuthenticationTestData.Hash("otro@comercio.local"));
        var otroRol = new Rol("owner", "Owner", null, otraEmpresa.Id);
        var otroAcceso = new AccesoUsuario(otroUsuario.Id, null, otraEmpresa.Id, otroRol);
        db.AddRange(segundoOperador, otraEmpresa, otroUsuario, otroRol, otroAcceso);
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
        var initial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var user = new Usuario(initial.UsuarioEmail, AuthenticationTestData.Hash(initial.UsuarioEmail));
        db.Add(user);
        await db.SaveChangesAsync();
        await AuthenticationTestData.Seeder(db, initial).SeedAsync();
        var access = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == user.Id);
        Assert.Equal(user.Id, access.UsuarioId);
        Assert.Equal(initial.ComercioId, access.ComercioId);
        Assert.Null(access.RolId); // Existing users are never elevated by bootstrap.
        Assert.Null(await new EfAccesosUsuarioRepository(db).ObtenerAsync(access.Id));
        db.Remove(access);
        await db.SaveChangesAsync();
        await AuthenticationTestData.Seeder(db, initial).SeedAsync();
        var repaired = await db.AccesosUsuario.SingleAsync(x => x.UsuarioId == user.Id);
        Assert.Equal(initial.ComercioId, repaired.ComercioId);
        Assert.Equal(user.Id, repaired.UsuarioId);
        Assert.Null(repaired.RolId);
        db.Entry(await db.Comercios.SingleAsync()).Property(x => x.Nombre).CurrentValue = "Comercio renombrado";
        db.Entry(await db.Operadores.SingleAsync()).Property(x => x.Nombre).CurrentValue = "Operador renombrado";
        await db.SaveChangesAsync();
        await AuthenticationTestData.Seeder(db, initial).SeedAsync();
        Assert.Equal("Comercio renombrado", (await db.Comercios.SingleAsync(x => x.Id == initial.ComercioId)).Nombre);
        Assert.Equal("Operador renombrado", (await db.Operadores.SingleAsync(x => x.Id == initial.OperadorId)).Nombre);
        Assert.Equal(1, await db.Comercios.CountAsync());
        Assert.Equal(1, await db.Operadores.CountAsync());
        Assert.Equal(2, await db.AccesosUsuario.CountAsync());
        var suspendedCommerce = await db.Comercios.SingleAsync(x => x.Id == initial.ComercioId);
        suspendedCommerce.CambiarEstado(EstadoOrganizacion.Suspendida);
        await db.SaveChangesAsync();
        await AuthenticationTestData.Seeder(db, initial).SeedAsync();
        Assert.Equal(EstadoOrganizacion.Suspendida,
            (await db.Comercios.SingleAsync(x => x.Id == initial.ComercioId)).Estado);
    }

    [Fact]
    public async Task Inicializacion_NoConvierteDatosHistoricosPorSusNombres()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var usuario = new Usuario("demo@nexoruta.local", AuthenticationTestData.Hash("demo@nexoruta.local"));
        var comercio = new Comercio("Comercio Demo");
        var operador = new Operador("Operador Demo");
        var role = new Rol("historical_owner", "Historical owner", null, comercio.Id);
        var acceso = new AccesoUsuario(usuario.Id, null, comercio.Id, role);
        db.AddRange(usuario, comercio, operador, role, acceso);
        await db.SaveChangesAsync();

        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
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
        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        db.AccesosUsuario.Add(new AccesoUsuario(operador.UsuarioId, null, comercio.ComercioId!.Value, await db.Roles.SingleAsync(x => x.Codigo == "propietario_comercio")));
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
        inicial.OperadorId = opUno.Id;

        var opDos = new Operador("Otro operador anterior");
        var comercio = new Comercio(inicial.ComercioNombre);
        inicial.ComercioId = comercio.Id;

        var usuario = new Usuario(
            inicial.UsuarioEmail,
            AuthenticationTestData.Hash(inicial.UsuarioEmail));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Usuarios\" (\"Id\", \"Email\") VALUES ({usuario.Id}, {usuario.Email})");

        var vinculoUno = Guid.CreateVersion7();
        var vinculoDos = Guid.CreateVersion7();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Operadores\" (\"Id\", \"Nombre\") VALUES ({opUno.Id}, {opUno.Nombre}), ({opDos.Id}, {opDos.Nombre})");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"Comercios\" (\"Id\", \"Nombre\") VALUES ({comercio.Id}, {comercio.Nombre})");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"OperadoresComercios\" (\"Id\", \"OperadorId\", \"ComercioId\") VALUES ({vinculoUno}, {opUno.Id}, {comercio.Id}), ({vinculoDos}, {opDos.Id}, {comercio.Id})");

        // La migración conserva el UUID menor entre las cuentas duplicadas.
        var cuentaAnterior = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        var segundoAcceso = Guid.Parse("018f0000-0000-7000-8000-000000000002");

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"OperadorId\", \"OperadorComercioId\") VALUES ({cuentaAnterior}, {usuario.Id}, {opUno.Id}, {vinculoUno}), ({segundoAcceso}, {usuario.Id}, {opDos.Id}, {vinculoDos})");

        var destinatario = new Destinatario(
            opUno.Id,
            "Destinatario anterior",
            "destinatario@example.test");

        var direccion = new Direccion(opUno.Id, "Direccion anterior");

        var envio = new Envio(
            opUno.Id,
            comercio.Id,
            usuario.Id,
            destinatario.Id,
            direccion.Id,
            opUno.Id);

        var bulto = new Bulto(
            opUno.Id,
            envio.Id,
            "ANTERIOR",
            1750m,
            32m,
            21m,
            11m);

        await InsertarDestinoAnterior(db, destinatario, direccion);
        await InsertarEnvioAnterior(db, envio, vinculoUno);
        await InsertarBultoAnterior(db, bulto);

        await migrator.MigrateAsync(actual);

        Assert.Equal(
            comercio.Id,
            await db.Database.SqlQuery<Guid>(
                $"SELECT \"ComercioId\" AS \"Value\" FROM \"AccesosUsuario\" WHERE \"Id\" = {cuentaAnterior}")
                .SingleAsync());

        Assert.Equal(
            usuario.Id,
            await db.Database.SqlQuery<Guid>(
                $"SELECT \"UsuarioId\" AS \"Value\" FROM \"AccesosUsuario\" WHERE \"Id\" = {cuentaAnterior}")
                .SingleAsync());

        var preCu01AccessIds = await db.Database.SqlQueryRaw<Guid>(
            "SELECT \"Id\" AS \"Value\" FROM \"AccesosUsuario\" ORDER BY \"Id\"")
            .ToArrayAsync();

        Assert.Equal(new[] { cuentaAnterior }, preCu01AccessIds);

        await MigrarActualConCorreosDeFixture(db, migrator);
        db.ChangeTracker.Clear();

        var historical = await db.AccesosUsuario
            .SingleAsync(x => x.UsuarioId == usuario.Id);

        Assert.Equal(cuentaAnterior, historical.Id);
        Assert.Equal(usuario.Id, historical.UsuarioId);
        Assert.Equal(comercio.Id, historical.ComercioId);
        Assert.Null(historical.OperadorId);
        Assert.Null(historical.RolId);

        Assert.Null(
            (await db.Usuarios.SingleAsync(x => x.Id == usuario.Id))
            .PasswordHash);

        Assert.Null(
            await new EfAccesosUsuarioRepository(db)
                .ObtenerAsync(cuentaAnterior));

        Assert.Equal(
            envio.Id,
            await db.Database.SqlQueryRaw<Guid>(
                "SELECT \"Id\" AS \"Value\" FROM \"Envios\"")
                .SingleAsync());

        Assert.False(historical.EsPropietario);

        await migrator.MigrateAsync(anterior);

        // El rollback reconstruye los vínculos respaldados por envíos.
        Assert.Equal(
            preCu01AccessIds,
            await db.Database.SqlQueryRaw<Guid>(
                "SELECT \"Id\" AS \"Value\" FROM \"AccesosUsuario\" ORDER BY \"Id\"")
                .ToArrayAsync());

        await MigrarActualConCorreosDeFixture(db, migrator);
        db.ChangeTracker.Clear();

        Assert.Equal(
            usuario.Id,
            (await db.AccesosUsuario.SingleAsync(
                x => x.Id == cuentaAnterior)).UsuarioId);

        await ConfigureHistoricAccounts(db, inicial);

        await VerificarEnvioHistoricoAsync(
            db, cuentaAnterior, envio, bulto);
    }

    [Fact]
    public async Task Migracion_EliminaElVinculoYConservaLosEnviosAlMigrarYRevertir()
    {
        const string anterior = "20261008180729_AccountOwnerAndShipmentOperator";

        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();

        var cuenta = await Cuenta(
            db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);

        var operador = await Cuenta(
            db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);

        // Solo en esta base efímera: habilita el downgrade histórico
        // sin intentar descartar una propiedad de operador registrada.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AccesosUsuario\" SET \"EsPropietario\" = FALSE WHERE \"OperadorId\" = {operador.OperadorId}");

        await migrator.MigrateAsync(anterior);

        var vinculo = Guid.CreateVersion7();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"OperadoresComercios\" (\"Id\", \"OperadorId\", \"ComercioId\") VALUES ({vinculo}, {operador.OperadorId!.Value}, {cuenta.ComercioId!.Value})");

        var destinatario = new Destinatario(
            operador.OperadorId.Value,
            "Destinatario anterior",
            "destinatario@example.test");

        var direccion = new Direccion(
            operador.OperadorId.Value,
            "Dirección anterior");

        await InsertarDestinoAnterior(db, destinatario, direccion);

        var envio = new Envio(
            operador.OperadorId.Value,
            cuenta.ComercioId.Value,
            cuenta.UsuarioId,
            destinatario.Id,
            direccion.Id,
            operador.OperadorId.Value);

        await InsertarEnvioAnterior(db, envio, vinculo);

        var bulto = new Bulto(
            envio.OperadorId,
            envio.Id,
            "ANTERIOR",
            1750m,
            32m,
            21m,
            11m);

        await InsertarBultoAnterior(db, bulto);

        await MigrarActualConCorreosDeFixture(db, migrator);
        await MigrarActualConCorreosDeFixture(db, migrator);

        Assert.False(
            await db.Database.SqlQueryRaw<bool>(
                "SELECT to_regclass('\"OperadoresComercios\"') IS NOT NULL AS \"Value\"")
                .SingleAsync());

        db.ChangeTracker.Clear();

        var migratedAccess = await db.AccesosUsuario
            .SingleAsync(x => x.Id == cuenta.AccesoId);

        Assert.Equal(cuenta.UsuarioId, migratedAccess.UsuarioId);
        Assert.Equal(cuenta.ComercioId, migratedAccess.ComercioId);
        Assert.Null(migratedAccess.RolId);

        Assert.Null(
            (await db.Usuarios.SingleAsync(
                x => x.Id == cuenta.UsuarioId)).PasswordHash);

        await ConfigureHistoricAccounts(db, inicial);

        await VerificarEnvioHistoricoAsync(
            db, cuenta.AccesoId, envio, bulto);

        await migrator.MigrateAsync(anterior);

        Assert.Equal(
            1,
            await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM \"OperadoresComercios\"")
                .SingleAsync());

        Assert.Equal(
            1,
            await db.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM \"Envios\" e JOIN \"OperadoresComercios\" r ON e.\"OperadorComercioId\" = r.\"Id\" AND e.\"OperadorId\" = r.\"OperadorId\"")
                .SingleAsync());

        await MigrarActualConCorreosDeFixture(db, migrator);
        db.ChangeTracker.Clear();

        await ConfigureHistoricAccounts(db, inicial);

        await VerificarEnvioHistoricoAsync(
            db, cuenta.AccesoId, envio, bulto);

        Assert.Equal(1, await db.Bultos.CountAsync());

        Assert.True(
            await db.Bultos.AnyAsync(
                x => x.Id == bulto.Id && x.EnvioId == envio.Id));
    }
    
    [Fact]
    public async Task Guardar_AggregateWithMismatchedTenantKeysWritesNothing()
    {
        await using var db = new NexoRutaDbContext(options);
        var inicial = Datos();
        await AuthenticationTestData.Seeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        var destinatario = new Destinatario(operador.OperadorId!.Value, "Ana", "destinatario@example.test");
        var direccion = new Direccion(operador.OperadorId.Value, "Rivera 123");
        var envio = new Envio(operador.OperadorId.Value, comercio.ComercioId!.Value,
            comercio.UsuarioId, destinatario.Id, direccion.Id, operador.OperadorId.Value);
        var bulto = new Bulto(Guid.CreateVersion7(), envio.Id, "FK-INVALIDA", 100m, 10m, 20m, 30m);

        var context = await new EfAccesosUsuarioRepository(db).ObtenerAsync(comercio.AccesoId)
            ?? throw new InvalidOperationException("The seeded commerce access is missing.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.ApplyAsync(RlsActorContext.From(context));
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new EfEnviosRepository(db).GuardarAsync(envio, destinatario, direccion, bulto));
        Assert.Contains("tenant keys", error.Message, StringComparison.OrdinalIgnoreCase);

        await using var consulta = new NexoRutaDbContext(options);
        Assert.Empty(await consulta.Envios.ToListAsync());
        Assert.Empty(await consulta.Bultos.ToListAsync());
        Assert.Empty(await consulta.Destinatarios.ToListAsync());
        Assert.Empty(await consulta.Direcciones.ToListAsync());
    }

    private static async Task ConfigureHistoricAccounts(NexoRutaDbContext db, AccesoInicial initial)
    {
        // Explicit test administration after crossing migrations that intentionally remove authentication schema.
        foreach (var (code, op, commerce) in new[] { ("administrador_operador", initial.OperadorId, (Guid?)null),
            ("propietario_comercio", (Guid?)null, initial.ComercioId) })
            if (!await db.Roles.AnyAsync(x => x.Codigo == code && x.OperadorId == op && x.ComercioId == commerce))
                db.Add(new Rol(code, code, op, commerce));
        await db.SaveChangesAsync();
        await AuthenticationTestData.Seeder(db, initial).SeedAsync();
        foreach (var access in await db.AccesosUsuario.ToListAsync())
        {
            var user = await db.Usuarios.SingleAsync(x => x.Id == access.UsuarioId);
            user.ActualizarPasswordHash(AuthenticationTestData.Hash(user.Email));
            access.AsignarRol(await db.Roles.SingleAsync(x => x.OperadorId == access.OperadorId && x.ComercioId == access.ComercioId
                && x.Codigo == (access.OperadorId.HasValue ? "administrador_operador" : "propietario_comercio")));
        }
        await db.SaveChangesAsync();
    }

    private static async Task InsertarDestinoAnterior(NexoRutaDbContext db, Destinatario destinatario, Direccion direccion)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Destinatarios\" (\"Id\", \"OperadorId\", \"Nombre\") VALUES ({destinatario.Id}, {destinatario.OperadorId}, {destinatario.Nombre})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Direcciones\" (\"Id\", \"OperadorId\", \"Descripcion\") VALUES ({direccion.Id}, {direccion.OperadorId}, {direccion.Descripcion})");
    }

    private static Task<int> InsertarBultoAnterior(NexoRutaDbContext db, Bulto bulto)
        => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Bultos\" (\"Id\", \"OperadorId\", \"EnvioId\", \"Codigo\", \"PesoGramos\", \"LargoCentimetros\", \"AnchoCentimetros\", \"AltoCentimetros\") VALUES ({bulto.Id}, {bulto.OperadorId}, {bulto.EnvioId}, {bulto.Codigo}, {bulto.PesoGramos}, {bulto.LargoCentimetros}, {bulto.AnchoCentimetros}, {bulto.AltoCentimetros})");

    private static Task<int> InsertarEnvioAnterior(NexoRutaDbContext db, Envio envio, Guid vinculoId)
        => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Envios\" (\"Id\", \"OperadorId\", \"OperadorComercioId\", \"CreadoPorUsuarioId\", \"DestinatarioId\", \"DireccionId\", \"Estado\") VALUES ({envio.Id}, {envio.OperadorId}, {vinculoId}, {envio.CreadoPorUsuarioId}, {envio.DestinatarioId}, {envio.DireccionId}, 'Admitido')");

    private ServiceProvider Servicios()
    {
        using var db = new NexoRutaDbContext(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["AccesoInicial:UsuarioEmail"] = "dueno@prueba.local",
            ["AccesoInicial:OperadorUsuarioEmail"] = "operador@prueba.local",
            ["AccesoInicial:OperadorNombre"] = "Operador de prueba",
            ["AccesoInicial:ComercioNombre"] = "Comercio de prueba"
        }).Build();
        return new ServiceCollection().AddNexoRutaPersistence(configuration).BuildServiceProvider();
    }

    private static AccesoInicial Datos()
    {
        var id = Guid.CreateVersion7().ToString("N");
        return new AccesoInicial($"dueno-{id}@nexoruta.local", $"Operador {id}", $"Comercio {id}", $"operador-{id}@nexoruta.local");
    }

    private static async Task<ContextoUsuario> Cuenta(NexoRutaDbContext db, string email, TipoAccesoUsuario tipo)
        => Assert.Single(await new EfAccesosUsuarioRepository(db).ListarAsync(tipo), x => x.UsuarioEmail == email);

    private static async Task<EnvioCreado> Crear(NexoRutaDbContext db, Guid accesoId, Guid operadorId, string codigo)
    {
        var context = await new EfAccesosUsuarioRepository(db).ObtenerAsync(accesoId)
            ?? throw new InvalidOperationException("The selected access is missing.");
        if (context.ComercioId is not null && await db.Operadores.AnyAsync(o => o.Id == operadorId))
            await ReglasEnvioTestData.PublicarInicialAsync(db, operadorId, context.UsuarioId);
        await using var transaction = await db.BeginOrJoinAsync();
        await db.ApplyAsync(RlsActorContext.From(context));
        var created = await new CrearEnvioUseCase(new EfEnviosRepository(db), new UsuarioSeleccionado(db, accesoId), new EfOperadoresRepository(db))
            .EjecutarAsync(new CrearEnvioCommand(
                OperadorId: operadorId,
                DestinatarioNombre: "Destinatario de prueba",
                DestinatarioEmail: "destinatario@example.test",
                Direccion: "Av. Rivera 123",
                CodigoBulto: codigo,
                PesoGramos: 1750m,
                LargoCentimetros: 32m,
                AnchoCentimetros: 21m,
                AltoCentimetros: 11m));
        await transaction.CommitAsync();
        return created;
    }

    private static async Task<IReadOnlyList<EnvioDetalle>> Listar(NexoRutaDbContext db, Guid accesoId)
    {
        var context = await new EfAccesosUsuarioRepository(db).ObtenerAsync(accesoId)
            ?? throw new InvalidOperationException("The selected access is missing.");
        await using var transaction = await db.BeginOrJoinAsync();
        await db.ApplyAsync(RlsActorContext.From(context));
        var shipments = await new ListarEnviosUseCase(new EfEnviosRepository(db), new UsuarioSeleccionado(db, accesoId)).EjecutarAsync();
        await transaction.CommitAsync();
        return shipments;
    }

    private sealed class UsuarioSeleccionado(NexoRutaDbContext db, Guid accesoId) : IUsuarioActual
    {
        public async Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => await new EfAccesosUsuarioRepository(db).ObtenerAsync(accesoId, cancellationToken)
                ?? throw new AccesoActualNoDisponibleException();
    }
    private static async Task MigrarActualConCorreosDeFixture(NexoRutaDbContext db, IMigrator migrator)
    {
        await HistoricRecipientEmail.MigrateAsync(db);
        // These round-trip fixtures stop before the breaking shipment-rules migration, while the
        // current repository projection includes the later shipment creation timestamp.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Envios\" ADD COLUMN IF NOT EXISTS \"CreadoEnUtc\" timestamp with time zone NOT NULL DEFAULT now()");
    }
    private static async Task VerificarEnvioHistoricoAsync(
        NexoRutaDbContext db,
        Guid accesoId,
        Envio esperado,
        Bulto bultoEsperado)
    {
        var contexto = await new EfAccesosUsuarioRepository(db)
            .ObtenerAsync(accesoId)
            ?? throw new InvalidOperationException(
                "The historical commerce access is missing.");

        Assert.Equal((Guid?)esperado.ComercioId, contexto.ComercioId);

        await using var transaction = await db.BeginOrJoinAsync();
        await db.ApplyAsync(RlsActorContext.From(contexto));

        var envio = Assert.Single(
            await db.Envios
                .AsNoTracking()
                .Where(x => x.ComercioId == esperado.ComercioId)
                .Select(x => new
                {
                    x.Id,
                    x.OperadorId,
                    x.ComercioId,
                    x.CreadoPorUsuarioId
                })
                .ToArrayAsync());

        Assert.Equal(esperado.Id, envio.Id);
        Assert.Equal(esperado.OperadorId, envio.OperadorId);
        Assert.Equal(esperado.ComercioId, envio.ComercioId);
        Assert.Equal(esperado.CreadoPorUsuarioId, envio.CreadoPorUsuarioId);

        var bulto = Assert.Single(
            await db.Bultos
                .AsNoTracking()
                .Where(x => x.EnvioId == esperado.Id)
                .Select(x => new
                {
                    x.Id,
                    x.EnvioId,
                    x.Codigo
                })
                .ToArrayAsync());

        Assert.Equal(bultoEsperado.Id, bulto.Id);
        Assert.Equal(esperado.Id, bulto.EnvioId);
        Assert.Equal(bultoEsperado.Codigo, bulto.Codigo);

        await transaction.CommitAsync();
    }
}
