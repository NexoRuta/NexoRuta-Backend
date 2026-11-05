using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NexoRuta.Api.Features.Envios.Contracts.Requests;
using NexoRuta.Api.Features.Reparto.Contracts;
using NexoRuta.Domain.Envios;
using NexoRuta.Domain.Tarifas;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.IntegrationTests.Seguridad;
using static NexoRuta.IntegrationTests.Fixtures.RepartoHttpData;
namespace NexoRuta.IntegrationTests.Reparto;
public sealed partial class OperacionesRepartoHttpTests
{
 [Theory]
 [InlineData(false)]
 [InlineData(true)]
 public async Task C37_RetiroPorApiIdempotenteEnRutaMixtaConFotoFirmaRecepcionYEntregaComercio(bool fallido)
 {
  var data=await postgres.CreateDatabaseAsync();
  using(var seed=data.CreateApiFactory())using(seed.CreateHttpClient()){}
  var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
  var settings=RlsRestrictedHttpFlowTests.ApiSettings(); settings["Reparto:EvidenciasEncryptionKey"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
  using var factory=new IntegrationApiFactory(runtime,settings:settings);using var client=factory.CreateHttpClient();
  var preparado=await Preparar(data,client);
  await using var db=data.CreateDbContext();
  var op=await db.AccesosUsuario.SingleAsync(a=>a.Id==preparado.AccesoId);var comercio=await db.AccesosUsuario.SingleAsync(a=>a.ComercioId!=null);
  await using(var tx=await db.Database.BeginTransactionAsync())
  {
   var root=await db.TarifariosOperador.SingleAsync(t=>t.OperadorId==op.OperadorId);
   var v=VersionTarifario.CrearBorrador(op.OperadorId!.Value,op.UsuarioId);db.Add(v);await db.SaveChangesAsync();
   v.ReemplazarConfiguracion(0,[new(op.OperadorId.Value,ModalidadTarifaria.Estandar,100,10,200,[]),new(op.OperadorId.Value,ModalidadTarifaria.Retiro,70,5,100,[])]);await db.SaveChangesAsync();
   v.Publicar(v.VersionConcurrencia,root.UltimoNumeroPublicacion+1,op.UsuarioId,new HashSet<Guid>{op.OperadorId.Value});await db.SaveChangesAsync();root.Publicar(v);await db.SaveChangesAsync();await tx.CommitAsync();
  }
  await client.SetBearerForAccessAsync(data,comercio.Id);
  var solicitud=new CrearEnvioRequest { OperadorId=op.OperadorId,DestinatarioNombre="Comprador sintético",DestinatarioEmail="comprador@example.test",Direccion="Domicilio sintético 123",
   Servicio=ServiciosEnvioTestData.Declarar(op.OperadorId),Retiro=new(){SolicitudId=Guid.NewGuid(),Motivo="Cambio de producto",DesdeUtc=DateTimeOffset.UtcNow.AddMinutes(1),HastaUtc=DateTimeOffset.UtcNow.AddDays(2)},
   Bultos=[new(){Codigo="RETIRO-UNO",PesoGramos=1000,LargoCentimetros=10,AnchoCentimetros=10,AltoCentimetros=10}] };
  solicitud.Servicio.Modalidad="Retiro";
  using var creada=await client.PostAsJsonAsync("/api/retiros",solicitud);Assert.True(creada.StatusCode==HttpStatusCode.Created,await creada.Content.ReadAsStringAsync());
  var payload=await creada.Content.ReadFromJsonAsync<JsonElement>();var retiroId=payload.GetProperty("id").GetGuid();
  using var repetida=await client.PostAsJsonAsync("/api/retiros",solicitud);Assert.Equal(HttpStatusCode.Created,repetida.StatusCode);Assert.Equal(retiroId,(await repetida.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
  Assert.Equal(2,await db.Envios.CountAsync());
  Assert.Equal("Retiro",payload.GetProperty("tarifa").GetProperty("modalidad").GetString());
  await client.SetBearerForAccessAsync(data,op.Id);
  using var plan=await client.PostAsJsonAsync("/api/rutas",new{RepartidorAccesoId=op.Id,preparado.VehiculoId,Fecha=FechaOperacion,Paradas=new[]{new{EnvioId=preparado.EnvioId,LlegadaEstimadaUtc=LlegadaEstimada},new{EnvioId=retiroId,LlegadaEstimadaUtc=LlegadaEstimada.AddMinutes(20)}}});
  Assert.True(plan.IsSuccessStatusCode,await plan.Content.ReadAsStringAsync());var hoja=(await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
  using var despacho=await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho",new{hoja.Version});Assert.Equal(HttpStatusCode.OK,despacho.StatusCode);hoja=(await despacho.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
  Assert.True(hoja.Paradas[1].EsRetiro);
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),1,"InicioJornada"))).Resultado);
  var entrega=hoja.Paradas[0];
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),2,"CargarBulto",entrega.EnvioId,entrega.EnvioVersion,new{BultoId=entrega.Bultos[0].Id}))).Resultado);
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),3,"ConfirmarCarga"))).Resultado);
  var retiro=hoja.Paradas[1];
  if(fallido)
  {
   Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),4,"RetiroFallido",retiroId,retiro.EnvioVersion,new{Motivo="AUSENTE"}))).Resultado);
   Assert.Equal(EstadoEnvio.RetiroFallido,(await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId)).Estado);
   Assert.Equal(EstadoCustodiaBulto.SinRecepcion,(await db.Bultos.SingleAsync(b=>b.EnvioId==retiroId)).Custodia);return;
  }
  var foto=Guid.NewGuid();var firma=Guid.NewGuid();
  foreach(var archivo in new[]{(foto,"Foto"),(firma,"Firma")})
  { using var upload=await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{archivo.Item1}",new{EnvioId=retiroId,Tipo=archivo.Item2,Contenido=FotoPrueba});Assert.Equal(HttpStatusCode.NoContent,upload.StatusCode); }
  var comando=Operacion(Guid.NewGuid(),4,"RetirarBultos",retiroId,retiro.EnvioVersion,new{FotoId=foto,FirmaId=firma,ResultadosBultos=new[]{new{BultoId=retiro.Bultos[0].Id,retiro.Bultos[0].Version,Entregado=true}}});
  var hecho=await Aplicar(client,hoja,comando);Assert.Equal("Aplicada",hecho.Resultado);Assert.Equal(JsonSerializer.Serialize(hecho),JsonSerializer.Serialize(await Aplicar(client,hoja,comando)));
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),5,"Rendicion",datos:new{BultoIds=new[]{retiro.Bultos[0].Id,entrega.Bultos[0].Id}}))).Resultado);
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),6,"FinJornada"))).Resultado);
  var e=await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId);
  using var recepcion=await client.PostAsJsonAsync($"/api/deposito/envios/{retiroId}/recepcion",new{e.Version});Assert.True(recepcion.StatusCode==HttpStatusCode.NoContent,await recepcion.Content.ReadAsStringAsync());
  e=await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId);Assert.Equal(EstadoEnvio.RetiroEnDeposito,e.Estado);
  using var final=await client.PostAsJsonAsync($"/api/deposito/retiros/{retiroId}/entrega-comercio",new{e.Version});Assert.Equal(HttpStatusCode.NoContent,final.StatusCode);
  Assert.Equal(EstadoEnvio.EntregadoComercio,(await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId)).Estado);
  Assert.Equal(EstadoCustodiaBulto.Comercio,(await db.Bultos.AsNoTracking().SingleAsync(b=>b.EnvioId==retiroId)).Custodia);
  Assert.Equal(foto,(await db.PruebasEntrega.SingleAsync(p=>p.EnvioId==retiroId)).FotoId);
 }
}
