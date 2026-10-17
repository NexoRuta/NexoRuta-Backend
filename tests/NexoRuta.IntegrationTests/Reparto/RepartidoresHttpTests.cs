using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Administracion;
using NexoRuta.Domain.Reparto;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using Npgsql;

namespace NexoRuta.IntegrationTests.Reparto;

public sealed class RepartidoresHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task AdministradorVinculaPerfilSinCrearUsuarioYVersionaCambios()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var usuariosAntes = await db.Usuarios.CountAsync();
        await client.SetBearerForAccessAsync(data, acceso.Id);
        using var alta = await Guardar(client, acceso.Id);
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        var perfil = (await alta.Content.ReadFromJsonAsync<RepartidorResponse>())!;
        Assert.Equal((acceso.Id, acceso.UsuarioId, acceso.OperadorId, 0L),
            (perfil.AccesoId, perfil.UsuarioId, (Guid?)perfil.OperadorId, perfil.Version));
        using var cambio = await Guardar(client, acceso.Id, "Nuevo nombre", version: 0);
        Assert.Equal(HttpStatusCode.OK, cambio.StatusCode);
        Assert.Equal(1, (await cambio.Content.ReadFromJsonAsync<RepartidorResponse>())!.Version);
        using var viejo = await Guardar(client, acceso.Id, "Nombre perdido", version: 0);
        Assert.Equal(HttpStatusCode.Conflict, viejo.StatusCode);
        var lista = (await client.GetFromJsonAsync<RepartidorResponse[]>("/api/repartidores"))!;
        Assert.Equal("Nuevo nombre", Assert.Single(lista).Nombre);
        Assert.Equal(usuariosAntes, await db.Usuarios.CountAsync());
    }

    [Fact]
    public async Task PerfilYPermisoSonNecesariosYDesactivacionImpideEjecutar()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        await client.SetBearerForAccessAsync(data, acceso.Id);
        using var sinAmbos = await client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.Forbidden, sinAmbos.StatusCode);
        using var crear = await Guardar(client, acceso.Id);
        Assert.Equal(HttpStatusCode.OK, crear.StatusCode);
        var permiso = await db.Permisos.SingleAsync(x => x.Codigo == "reparto.ejecutar");
        db.RolesPermisos.Remove(await db.RolesPermisos.SingleAsync(x => x.RolId == acceso.RolId && x.PermisoId == permiso.Id));
        await db.SaveChangesAsync();
        using var sinPermiso = await client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.Forbidden, sinPermiso.StatusCode);
        db.RolesPermisos.Add(new RolPermiso(acceso.RolId!.Value, permiso.Id));
        await db.SaveChangesAsync();
        using var conAmbos = await client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.OK, conAmbos.StatusCode);
        using var desactivar = await client.PutAsJsonAsync($"/api/repartidores/{acceso.Id}", new { Nombre = "Repartidor", Activo = false, Version = 0 });
        Assert.Equal(HttpStatusCode.OK, desactivar.StatusCode);
        using var desactivado = await client.GetAsync("/api/mobile/perfil");
        Assert.Equal(HttpStatusCode.Forbidden, desactivado.StatusCode);
    }

    [Fact]
    public async Task OtroOperadorYComercioNoPuedenLeerNiVincularAccesoAjeno()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var propio = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var ajeno = await CrearOtro(db);
        await client.SetBearerForAccessAsync(data, propio.Id);
        using var altaAjena = await Guardar(client, ajeno.Id);
        Assert.Equal(HttpStatusCode.NotFound, altaAjena.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Guardar(client, propio.Id)).StatusCode);
        await client.SetBearerForAccessAsync(data, ajeno.Id);
        Assert.Empty((await client.GetFromJsonAsync<RepartidorResponse[]>("/api/repartidores"))!);
        using var modificarAjeno = await Guardar(client, propio.Id, version: 0);
        Assert.Equal(HttpStatusCode.NotFound, modificarAjeno.StatusCode);
        var comercio = await db.AccesosUsuario.SingleAsync(x => x.ComercioId != null);
        await client.SetBearerForAccessAsync(data, comercio.Id);
        using var listaComercio = await client.GetAsync("/api/repartidores");
        Assert.Equal(HttpStatusCode.Forbidden, listaComercio.StatusCode);
    }

    [Fact]
    public async Task EscriturasConcurrentesNoSobrescribenLaMismaVersion()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        await client.SetBearerForAccessAsync(data, acceso.Id);
        using var alta = await Guardar(client, acceso.Id);
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        var respuestas = await Task.WhenAll(Guardar(client, acceso.Id, "Primero", 0), Guardar(client, acceso.Id, "Segundo", 0));
        try
        {
            Assert.Single(respuestas, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(respuestas, x => x.StatusCode == HttpStatusCode.Conflict);
        }
        finally { foreach (var r in respuestas) r.Dispose(); }
    }

    [Fact]
    public async Task ClaveCompuestaImpideMezclarPerfilConAccesoDeOtroOperadorEnSql()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var propio = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        var ajeno = await CrearOtro(db);
        db.Repartidores.Add(new Repartidor(ajeno.Id, propio.OperadorId!.Value, "Incorrecto"));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task RlsConRolRestringidoDeniegaSinContextoYFiltraLecturasYEscrituras()
    {
        var data = await postgres.CreateDatabaseAsync();
        using var factory = data.CreateApiFactory();
        using var client = factory.CreateHttpClient();
        await using var db = data.CreateDbContext();
        var acceso = await db.AccesosUsuario.SingleAsync(x => x.OperadorId != null);
        await client.SetBearerForAccessAsync(data, acceso.Id);
        using var alta = await Guardar(client, acceso.Id);
        Assert.Equal(HttpStatusCode.OK, alta.StatusCode);
        await using var conn = new NpgsqlConnection(data.ConnectionString);
        await conn.OpenAsync();
        var rol = "reparto_" + Guid.NewGuid().ToString("N");
        await using (var crear = new NpgsqlCommand($"CREATE ROLE \"{rol}\" NOLOGIN NOSUPERUSER NOBYPASSRLS; GRANT SELECT,INSERT,UPDATE,DELETE ON \"Repartidores\" TO \"{rol}\"", conn))
            await crear.ExecuteNonQueryAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cambiar = new NpgsqlCommand($"SET LOCAL ROLE \"{rol}\"", conn, tx)) await cambiar.ExecuteNonQueryAsync();
        Assert.Equal(0L, await Contar(conn, tx));
        await Contexto(conn, tx, acceso.OperadorId!.Value);
        Assert.Equal(1L, await Contar(conn, tx));
        await using (var mover = new NpgsqlCommand("UPDATE \"Repartidores\" SET \"OperadorId\" = @otro", conn, tx))
        {
            mover.Parameters.AddWithValue("otro", Guid.NewGuid());
            var ex = await Assert.ThrowsAsync<PostgresException>(() => mover.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
        await tx.RollbackAsync();
        await using var otraTx = await conn.BeginTransactionAsync();
        await using (var cambiar = new NpgsqlCommand($"SET LOCAL ROLE \"{rol}\"", conn, otraTx)) await cambiar.ExecuteNonQueryAsync();
        await Contexto(conn, otraTx, Guid.NewGuid());
        Assert.Equal(0L, await Contar(conn, otraTx));
        await otraTx.RollbackAsync();
    }

    private static Task<HttpResponseMessage> Guardar(HttpClient client, Guid acceso, string nombre = "Repartidor", long? version = null) =>
        client.PutAsJsonAsync($"/api/repartidores/{acceso}", new { Nombre = nombre, Activo = true, Version = version });

    private static async Task<AccesoUsuario> CrearOtro(NexoRutaDbContext db)
    {
        var op = new Operador("Otro operador");
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var usuario = new Usuario(email, AuthenticationTestData.Hash(email));
        var rol = new Rol("gestor", "Gestor", op.Id, null);
        var acceso = new AccesoUsuario(usuario.Id, op.Id, null, rol);
        var permiso = await db.Permisos.SingleAsync(x => x.Codigo == "usuarios.gestionar");
        db.AddRange(op, usuario, rol, new RolPermiso(rol.Id, permiso.Id));
        await db.SaveChangesAsync();
        db.Add(acceso);
        await db.SaveChangesAsync();
        return acceso;
    }

    private static async Task<long> Contar(NpgsqlConnection c, NpgsqlTransaction tx)
    {
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM \"Repartidores\"", c, tx);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task Contexto(NpgsqlConnection c, NpgsqlTransaction tx, Guid op)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('nexoruta.operador_id', @operador, true)", c, tx);
        cmd.Parameters.AddWithValue("operador", op.ToString());
        await cmd.ExecuteScalarAsync();
    }
}
