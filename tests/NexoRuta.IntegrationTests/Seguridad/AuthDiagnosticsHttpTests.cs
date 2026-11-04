using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using NexoRuta.Api.Core.Security;
using NexoRuta.Application.Administracion.Results;
using NexoRuta.IntegrationTests.Fixtures;
namespace NexoRuta.IntegrationTests.Seguridad;
public sealed class AuthDiagnosticsHttpTests(PostgresFixture postgres):IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task RealAuthenticationClassifiesExpectedRejectionsCorrelatesThemAndNeverLogsCredentials()
    {
        var data=await postgres.CreateDatabaseAsync();using(var seed=data.CreateShippingApiFactory())using(seed.CreateHttpClient()){}
        await using var db=data.CreateDbContext();var access=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);var user=await db.Usuarios.SingleAsync(u=>u.Id==access.UsuarioId);
        var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);var logs=new AuthDecisionCapture();
        using var factory=new IntegrationApiFactory(runtime,settings:RlsRestrictedHttpFlowTests.ApiSettings(),configureTestServices:s=>{s.AddSingleton(logs.For<JwtValidacionEventos>());s.AddSingleton(logs.For<NexoRuta.Api.Controllers.AuthController>());});using var client=factory.CreateHttpClient();
        var session=new SesionIniciada(user.Id,access.Id,user.Email,access.VersionSesion,VersionCredencial:user.VersionCredencial);
        string Token(DateTimeOffset now,Dictionary<string,string?>? config=null)=>new JwtTokenService(new JwtConfiguracion(new ConfigurationBuilder().AddInMemoryCollection(config??AuthenticationTestData.Settings()).Build()),new Clock(now)).Generar(session).Token;
        var good=Token(DateTimeOffset.UtcNow);
        async Task AssertRejection(string? token,int status,string[] reasons,string path="/api/usuarios/actual")
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,path);if(token is not null)request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await client.SendAsync(request);Assert.Equal(status,(int)response.StatusCode);
            var trace=Assert.Single(response.Headers.GetValues("X-NexoRuta-Auth-Trace"));Assert.True(Guid.TryParseExact(trace,"N",out _));
            var record=logs.Rows.Last();Assert.Equal(trace,record["AuthTrace"]);Assert.Contains((string)record["Reason"]!,reasons);Assert.Equal(status,record["Status"]);
            Assert.IsType<DateTimeOffset>(record["ServerUtc"]);Assert.False(((string)record["Route"]!).Contains('?'));
        }
        await AssertRejection(null,401,["missing_bearer"]);
        await AssertRejection("malformed-token",401,["invalid_token","invalid_bearer"]);
        var expired=Token(DateTimeOffset.UtcNow.AddMinutes(-20));await AssertRejection(expired,401,["token_expired"]);
        await AssertRejection(Token(DateTimeOffset.UtcNow.AddMinutes(2)),401,["token_not_yet_valid"]);
        var issuer=AuthenticationTestData.Settings();issuer["Jwt:Issuer"]="wrong-issuer";await AssertRejection(Token(DateTimeOffset.UtcNow,issuer),401,["invalid_issuer"]);
        var audience=AuthenticationTestData.Settings();audience["Jwt:Audience"]="wrong-audience";await AssertRejection(Token(DateTimeOffset.UtcNow,audience),401,["invalid_audience"]);
        var signature=AuthenticationTestData.Settings();signature["Jwt:SigningKeyBase64"]=Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await AssertRejection(Token(DateTimeOffset.UtcNow,signature),401,["invalid_signature","signature_key_unavailable"]);
        using(var login=await client.PostAsJsonAsync("/api/auth/login",new{Email=user.Email,Password="incorrect-synthetic-password"}))
        {Assert.Equal(HttpStatusCode.Unauthorized,login.StatusCode);Assert.Equal("login_rejected",logs.Rows.Last()["Reason"]);}
        await AssertRejection(good,403,["permission_denied"],"/api/rutas?fecha=2026-11-03");
        using(var request=new HttpRequestMessage(HttpMethod.Get,"/api/usuarios/actual")){request.Headers.Authorization=new("Bearer",good);using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);}
        access.RevocarSesiones();await db.SaveChangesAsync();await AssertRejection(good,401,["access_context_changed"]);
        var serialized=JsonSerializer.Serialize(logs.Rows);
        foreach(var secret in new[]{good,expired,AuthenticationTestData.Password,user.Email})Assert.False(serialized.Contains(secret,StringComparison.Ordinal),"Authentication diagnostics exposed credential data.");
        Assert.Equal(logs.Rows.Count,logs.Rows.Select(r=>r["AuthTrace"]).Distinct().Count());
    }
    private sealed class Clock(DateTimeOffset now):TimeProvider {public override DateTimeOffset GetUtcNow()=>now;}
}
