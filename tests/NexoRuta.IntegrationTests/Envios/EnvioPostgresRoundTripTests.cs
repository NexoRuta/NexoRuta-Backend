using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NexoRuta.Application.Administracion;
using NexoRuta.Application.Envios;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Envios;
using NexoRuta.Infrastructure.Administracion;
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
        var primero = Assert.Single(await cuentas.ListarOperadoresAsync(cuenta.ComercioId!.Value));
        var segundo = new Operador("Otro operador disponible");
        var segundoVinculo = new OperadorComercio(segundo.Id, cuenta.ComercioId.Value);
        db.AddRange(segundo, segundoVinculo);
        await db.SaveChangesAsync();
        Assert.Equal(2, (await cuentas.ListarOperadoresAsync(cuenta.ComercioId.Value)).Count);
        Assert.Single(await cuentas.ListarAsync(TipoAccesoUsuario.Comercio));

        var uno = await Crear(db, cuenta.AccesoId, primero.OperadorId, "PRIMERO");
        var dos = await Crear(db, cuenta.AccesoId, segundo.Id, "SEGUNDO");

        Assert.Equal(cuenta.UsuarioId, uno.CreadoPorUsuarioId);
        Assert.Equal(cuenta.UsuarioId, dos.CreadoPorUsuarioId);
        Assert.Equal(primero.OperadorId, uno.OperadorId);
        Assert.Equal(segundo.Id, dos.OperadorId);
        Assert.Equal(segundoVinculo.Id, dos.OperadorComercioId);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        Assert.Equal(cuenta, await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio));

        await using var consulta = new NexoRutaDbContext(options);
        var listados = await Listar(consulta, cuenta.AccesoId);
        Assert.Equal(2, listados.Count);
        var persistido = Assert.Single(listados, x => x.Id == dos.Id);
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
    public async Task Alta_RechazaOperadorNoDisponibleYCuentaDeOperadorSinGuardar()
    {
        var inicial = Datos();
        await using var db = new NexoRutaDbContext(options);
        await new DatosInicialesSeeder(db, inicial).SeedAsync();
        var comercio = await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio);
        var operador = await Cuenta(db, inicial.OperadorUsuarioEmail, TipoAccesoUsuario.Operador);
        var externo = new Operador("Operador sin relacion comercial");
        db.Operadores.Add(externo);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<OperadorNoVinculadoException>(() => Crear(db, comercio.AccesoId, externo.Id, "DENEGADO"));
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
        db.AddRange(segundoOperador, otraEmpresa, otroUsuario, otroAcceso,
            new OperadorComercio(segundoOperador.Id, comercio.ComercioId!.Value),
            new OperadorComercio(operador.OperadorId!.Value, otraEmpresa.Id));
        await db.SaveChangesAsync();

        var propioUno = await Crear(db, comercio.AccesoId, operador.OperadorId.Value, "PROPIO-UNO");
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
        var vinculoUno = new OperadorComercio(opUno.Id, comercio.Id);
        var vinculoDos = new OperadorComercio(opDos.Id, comercio.Id);
        db.AddRange(opUno, opDos, comercio, usuario, vinculoUno, vinculoDos);
        await db.SaveChangesAsync();
        // ponytail: migration retains the lowest UUID; same-millisecond UUIDv7 calls are not ordered.
        var cuentaAnterior = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        var segundoAcceso = Guid.Parse("018f0000-0000-7000-8000-000000000002");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"AccesosUsuario\" (\"Id\", \"UsuarioId\", \"OperadorId\", \"OperadorComercioId\") VALUES ({cuentaAnterior}, {usuario.Id}, {opUno.Id}, {vinculoUno.Id}), ({segundoAcceso}, {usuario.Id}, {opDos.Id}, {vinculoDos.Id})");
        var destinatario = new Destinatario(opUno.Id, "Destinatario anterior");
        var direccion = new Direccion(opUno.Id, "Direccion anterior");
        var envio = new Envio(opUno.Id, vinculoUno.Id, usuario.Id, destinatario.Id, direccion.Id);
        var bulto = new Bulto(opUno.Id, envio.Id, "ANTERIOR", 1750m, 32m, 21m, 11m);
        db.AddRange(destinatario, direccion, envio, bulto);
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
        Assert.Equal(2, (await new EfAccesosUsuarioRepository(db).ListarOperadoresAsync(comercio.Id)).Count);
        Assert.Equal(envio.Id, Assert.Single(await Listar(db, cuenta.AccesoId)).Id);

        await migrator.MigrateAsync(anterior);
        Assert.Equal(3, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AccesosUsuario\"").SingleAsync());
        await migrator.MigrateAsync(actual);
        Assert.Equal(usuario.Id, (await Cuenta(db, inicial.UsuarioEmail, TipoAccesoUsuario.Comercio)).UsuarioId);
        Assert.Equal(envio.Id, Assert.Single(await Listar(db, cuenta.AccesoId)).Id);
    }

    private static AccesoInicial Datos()
    {
        var id = Guid.CreateVersion7().ToString("N");
        return new AccesoInicial($"dueno-{id}@nexoruta.local", $"Operador {id}", $"Comercio {id}", $"operador-{id}@nexoruta.local");
    }

    private static async Task<ContextoUsuario> Cuenta(NexoRutaDbContext db, string email, TipoAccesoUsuario tipo)
        => Assert.Single(await new EfAccesosUsuarioRepository(db).ListarAsync(tipo), x => x.UsuarioEmail == email);

    private static Task<EnvioCreado> Crear(NexoRutaDbContext db, Guid accesoId, Guid operadorId, string codigo)
        => new CrearEnvioUseCase(new EfEnviosRepository(db), new UsuarioSeleccionado(db, accesoId), new EfAccesosUsuarioRepository(db))
            .EjecutarAsync(new CrearEnvioCommand(operadorId, "Destinatario de prueba", "Av. Rivera 123", codigo, 1750m, 32m, 21m, 11m));

    private static Task<IReadOnlyList<EnvioDetalle>> Listar(NexoRutaDbContext db, Guid accesoId)
        => new ListarEnviosUseCase(new EfEnviosRepository(db), new UsuarioSeleccionado(db, accesoId)).EjecutarAsync();

    private sealed class UsuarioSeleccionado(NexoRutaDbContext db, Guid accesoId) : IUsuarioActual
    {
        public async Task<ContextoUsuario> ObtenerAsync(CancellationToken cancellationToken = default)
            => await new EfAccesosUsuarioRepository(db).ObtenerAsync(accesoId, cancellationToken)
                ?? throw new AccesoActualNoDisponibleException();
    }
}
