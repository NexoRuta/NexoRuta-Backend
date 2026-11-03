using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.Infrastructure.Administracion;
using UglyToad.PdfPig;

namespace NexoRuta.IntegrationTests.Envios;

public sealed class EtiquetasEnvioHttpTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void LongPersistedTextRemainsCompleteReadableAndInsideThePage()
    {
        var parcel = Guid.NewGuid();
        var details = new NexoRuta.Application.Envios.Results.EnvioDetalle(Guid.NewGuid(),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic@example.test", new string('W', 160), new string('W', 160)),
            new string('W', 160), new string('W', 240), "Admitido", 1,
            [new(parcel, new string('W', 80), 999999999.99m, 999999999.99m, 999999999.99m, 999999999.99m)], DateTimeOffset.UtcNow);
        using var pdf = PdfDocument.Open(NexoRuta.Api.Features.Envios.Etiquetas.EtiquetasEnvioPdf.Generar(details));
        var page = pdf.GetPage(1);
        Assert.Equal(100 * 72 / 25.4, page.Width, 3);
        Assert.True(page.Height >= 150 * 72 / 25.4);
        Assert.Contains(parcel.ToString("D"), page.Text);
        Assert.All(page.Letters, letter =>
        {
            Assert.InRange(letter.GlyphRectangle.Left, 0, page.Width);
            Assert.InRange(letter.GlyphRectangle.Right, 0, page.Width);
            Assert.InRange(letter.GlyphRectangle.Bottom, 0, page.Height);
            Assert.InRange(letter.GlyphRectangle.Top, 0, page.Height);
        });
    }

    [Fact]
    public async Task OwnerDownloadsEveryPersistedParcelWithoutChangingShipmentState()
    {
        var data = await postgres.CreateDatabaseAsync();
        using (var seed = data.CreateShippingApiFactory()) using (seed.CreateHttpClient()) { }
        await using var db = data.CreateDbContext();
        var op = await db.Operadores.Select(o => o.Id).SingleAsync();
        var ownerAccess = await db.AccesosUsuario.SingleAsync(a => a.ComercioId != null);
        var operatorUserId = await db.AccesosUsuario.Where(a => a.OperadorId == op).Select(a => a.UsuarioId).SingleAsync();
        var operatorEmail = (await db.Usuarios.SingleAsync(u => u.Id == operatorUserId)).Email!;
        var foreign = new List<Guid>();
        foreach (var sameOperator in new[] { true, false })
        {
            var initial = new AccesoInicial($"labels-other-{sameOperator}@example.test", "Other operator", "Other commerce", sameOperator ? operatorEmail : $"labels-operator-{sameOperator}@example.test");
            if (sameOperator) initial.OperadorId = op;
            await AuthenticationTestData.Seeder(db, initial).SeedAsync();
            foreign.Add(await db.AccesosUsuario.Where(a => a.ComercioId == initial.ComercioId).Select(a => a.Id).SingleAsync());
        }
        var runtime = await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
        using var factory = new IntegrationApiFactory(runtime, settings: RlsRestrictedHttpFlowTests.ApiSettings());
        using var client = factory.CreateHttpClient();
        await client.SetBearerForAccessAsync(data, ownerAccess.Id);
        var ownerAuthorization = client.DefaultRequestHeaders.Authorization;
        var request = new CrearEnvioRequest
        {
            OperadorId = op, DestinatarioNombre = "Synthetic recipient", DestinatarioEmail = "labels@example.test", Direccion = "Synthetic address 538",
            Servicio = ServiciosEnvioTestData.Declarar(op),
            Bultos = Enumerable.Range(1, 3).Select(i => new BultoDeclaradoRequest { Codigo = $"PARCEL-{i}", PesoGramos = 1500, LargoCentimetros = 30, AnchoCentimetros = 20, AltoCentimetros = 10 }).ToList()
        };
        using var created = await client.PostAsJsonAsync("/api/envios", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var before = await client.GetStringAsync("/api/envios");
        var outbox = await db.OutboxMessages.CountAsync();
        using var pdf = await client.GetAsync($"/api/envios/{id}/etiquetas");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", pdf.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(pdf.Headers.CacheControl!.NoStore);
        Assert.True((await pdf.Content.ReadAsByteArrayAsync()).AsSpan(0, 5).SequenceEqual("%PDF-"u8));
        using var document = PdfDocument.Open(await pdf.Content.ReadAsByteArrayAsync());
        Assert.Equal(3, document.NumberOfPages);
        var parcels = await db.Bultos.Where(b => b.EnvioId == id).OrderBy(b => b.Codigo).ToArrayAsync();
        foreach (var (page, index) in document.GetPages().Select((p, i) => (p, i)))
        {
            Assert.Equal(100 * 72 / 25.4, page.Width, 3);
            Assert.Equal(150 * 72 / 25.4, page.Height, 3);
            Assert.Contains(parcels[index].Id.ToString("D"), page.Text);
            Assert.Contains(parcels[index].Codigo, page.Text);
            Assert.All(page.Letters, letter =>
            {
                Assert.InRange(letter.GlyphRectangle.Left, 0, page.Width);
                Assert.InRange(letter.GlyphRectangle.Right, 0, page.Width);
                Assert.InRange(letter.GlyphRectangle.Bottom, 0, page.Height);
                Assert.InRange(letter.GlyphRectangle.Top, 0, page.Height);
            });
        }
        Assert.Equal(before, await client.GetStringAsync("/api/envios"));
        Assert.Equal(outbox, await db.OutboxMessages.CountAsync());
        using var again = await client.GetAsync($"/api/envios/{id}/etiquetas");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        foreach (var access in foreign)
        {
            await client.SetBearerForAccessAsync(data, access);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/envios")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/envios/{id}/etiquetas")).StatusCode);
        }
        var otherOperator = await db.AccesosUsuario.Where(a => a.OperadorId != null && a.OperadorId != op).Select(a => a.Id).FirstAsync();
        await client.SetBearerForAccessAsync(data, otherOperator);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/envios/{id}/etiquetas")).StatusCode);
        var operatorAccess = await db.AccesosUsuario.FirstAsync(a => a.OperadorId == op);
        await client.SetBearerForAccessAsync(data, operatorAccess.Id);
        var operatorAuthorization = client.DefaultRequestHeaders.Authorization;
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/envios/{Guid.NewGuid()}/etiquetas")).StatusCode);
        using var operatorPdf = await client.GetAsync($"/api/envios/{id}/etiquetas");
        Assert.Equal(HttpStatusCode.OK, operatorPdf.StatusCode);
        Assert.True(operatorPdf.Headers.CacheControl!.NoStore);
        using var operatorDocument = PdfDocument.Open(await operatorPdf.Content.ReadAsByteArrayAsync());
        Assert.Equal(document.GetPages().Select(p => p.Text), operatorDocument.GetPages().Select(p => p.Text));
        Assert.Equal(outbox, await db.OutboxMessages.CountAsync());
        client.DefaultRequestHeaders.Authorization = ownerAuthorization;
        Assert.Equal(before, await client.GetStringAsync("/api/envios"));
        var readPermission = await db.Permisos.SingleAsync(p => p.Codigo == "envios.consultar");
        db.RolesPermisos.Remove(await db.RolesPermisos.SingleAsync(p => p.RolId == operatorAccess.RolId && p.PermisoId == readPermission.Id));
        await db.SaveChangesAsync();
        client.DefaultRequestHeaders.Authorization = operatorAuthorization;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/envios/{id}/etiquetas")).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/envios/{id}/etiquetas")).StatusCode);
    }
}
