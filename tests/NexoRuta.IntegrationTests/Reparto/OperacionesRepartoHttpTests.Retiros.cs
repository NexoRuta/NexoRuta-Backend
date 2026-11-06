using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
 [InlineData(false, 1)]
 [InlineData(false, 500)]
 [InlineData(false, 501)]
 [InlineData(false, 1500)]
 [InlineData(true, 1)]
 public async Task C37_RetiroPorApiIdempotenteEnRutaMixtaConFotoFirmaRecepcionYEntregaComercio(bool fallido, int cantidad)
 {
  var data=await postgres.CreateDatabaseAsync();
  using(var seed=data.CreateApiFactory())using(seed.CreateHttpClient()){}
  var runtime=await RlsRestrictedHttpFlowTests.RestrictApiAsync(data);
  var settings=RlsRestrictedHttpFlowTests.ApiSettings(); settings["Reparto:EvidenciasEncryptionKey"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
  settings["RoutingGeoapify:PermitirLlamadasReales"]="true";settings["RoutingGeoapify:ApiKey"]="SYNTHETIC-NOT-A-CREDENTIAL";
  using var factory=new IntegrationApiFactory(runtime,settings:settings);
  using var host=factory.WithWebHostBuilder(b=>b.ConfigureTestServices(s=>s.AddHttpClient("RoutingGeoapify").ConfigurePrimaryHttpMessageHandler(()=>new RoutingBultosControlado())));
  using var client=host.CreateClient();
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
  var llegada=DateTimeOffset.UtcNow.AddMinutes(15);
  var solicitud=new CrearEnvioRequest { OperadorId=op.OperadorId,DestinatarioNombre="Comprador sintético",DestinatarioEmail="comprador@example.test",Direccion="Domicilio sintético 123",
   Servicio=ServiciosEnvioTestData.Declarar(op.OperadorId),Retiro=new(){SolicitudId=Guid.NewGuid(),Motivo="Cambio de producto",DesdeUtc=DateTimeOffset.UtcNow.AddMinutes(1),HastaUtc=DateTimeOffset.UtcNow.AddDays(2)},
   Bultos=Enumerable.Range(1,cantidad).Select(i => new BultoDeclaradoRequest { Codigo=$"RETIRO-{i}",PesoGramos=0.01m,LargoCentimetros=0.1m,AnchoCentimetros=0.1m,AltoCentimetros=0.1m }).ToList() };
  solicitud.Servicio.Modalidad="Retiro";
  using var creada=await client.PostAsJsonAsync("/api/retiros",solicitud);Assert.True(creada.StatusCode==HttpStatusCode.Created,await creada.Content.ReadAsStringAsync());
  var payload=await creada.Content.ReadFromJsonAsync<JsonElement>();var retiroId=payload.GetProperty("id").GetGuid();
  using var repetida=await client.PostAsJsonAsync("/api/retiros",solicitud);Assert.Equal(HttpStatusCode.Created,repetida.StatusCode);Assert.Equal(retiroId,(await repetida.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
  Assert.Equal(2,await db.Envios.CountAsync());
  Assert.Equal("Retiro",payload.GetProperty("tarifa").GetProperty("modalidad").GetString());
  await client.SetBearerForAccessAsync(data,op.Id);
  using var limitar=await client.PutAsJsonAsync("/api/reparto/vehiculos",new{Id=preparado.VehiculoId,Version=(await db.Vehiculos.AsNoTracking().SingleAsync(v=>v.Id==preparado.VehiculoId)).Version,Matricula="SAB1234",PesoMaximoGramos=1000m,VolumenMaximoCm3=1000m,Activo=true});
  Assert.True(limitar.IsSuccessStatusCode,await limitar.Content.ReadAsStringAsync());
  await ConfigurarBaseVial(client,preparado.VehiculoId);
  using var plan=await client.PostAsJsonAsync("/api/rutas",new{RepartidorAccesoId=op.Id,preparado.VehiculoId,Fecha=FechaOperacion,Paradas=new[]{new{EnvioId=preparado.EnvioId,LlegadaEstimadaUtc=llegada},new{EnvioId=retiroId,LlegadaEstimadaUtc=llegada.AddMinutes(20)}}});
  Assert.True(plan.IsSuccessStatusCode,await plan.Content.ReadAsStringAsync());var hoja=(await plan.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
  using var despacho=await client.PostAsJsonAsync($"/api/rutas/{hoja.Id}/despacho",new{hoja.Version});Assert.Equal(HttpStatusCode.OK,despacho.StatusCode);hoja=(await despacho.Content.ReadFromJsonAsync<HojaRutaResponse>())!;
  Assert.True(hoja.Paradas[1].EsRetiro);
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),1,"InicioJornada"))).Resultado);
  var entrega=hoja.Paradas[0];
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),2,"CargarBulto",entrega.EnvioId,entrega.EnvioVersion,new{BultoId=entrega.Bultos[0].Id}))).Resultado);
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,new { Id=Guid.NewGuid(), Secuencia=3, Tipo="ConfirmarCarga", OcurridaEn=DateTimeOffset.UtcNow, Datos=new {} })).Resultado);
  var retiro=hoja.Paradas[1];
  await VerificarRecorridoBultos(data,client,hoja,DateTimeOffset.UtcNow,4,entrega.EnvioId,retiroId);
  if(fallido)
  {
   Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),5,"RetiroFallido",retiroId,retiro.EnvioVersion,new{Motivo="AUSENTE"}))).Resultado);
   Assert.Equal(EstadoEnvio.RetiroFallido,(await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId)).Estado);
   Assert.Equal(EstadoCustodiaBulto.SinRecepcion,(await db.Bultos.SingleAsync(b=>b.EnvioId==retiroId)).Custodia);
   using var reprogramar=await client.PostAsJsonAsync("/api/rutas",new{RepartidorAccesoId=op.Id,preparado.VehiculoId,Fecha=FechaOperacion.AddDays(1),Paradas=new[]{new{EnvioId=retiroId,LlegadaEstimadaUtc=llegada.AddDays(1)}}});
   Assert.True(reprogramar.IsSuccessStatusCode,await reprogramar.Content.ReadAsStringAsync());
   Assert.Equal(EstadoEnvio.RetiroAsignado,(await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId)).Estado);
   Assert.Equal(EstadoCustodiaBulto.SinRecepcion,(await db.Bultos.AsNoTracking().SingleAsync(b=>b.EnvioId==retiroId)).Custodia);return;
  }
  var foto=Guid.NewGuid();var firma=Guid.NewGuid();
  foreach(var archivo in new[]{(foto,"Foto"),(firma,"Firma")})
  { using var upload=await client.PutAsJsonAsync($"/api/mobile/jornadas/{hoja.JornadaId}/evidencias/{archivo.Item1}",new{EnvioId=retiroId,Tipo=archivo.Item2,Contenido=FotoPrueba});Assert.Equal(HttpStatusCode.NoContent,upload.StatusCode); }
  var comando=Operacion(Guid.NewGuid(),5,"RetirarBultos",retiroId,retiro.EnvioVersion,new{FotoId=foto,FirmaId=firma,ResultadosBultos=retiro.Bultos.Select(b=>new{BultoId=b.Id,b.Version,Entregado=true}).ToArray()});
  var rechazo=await Aplicar(client,hoja,comando);Assert.Equal("Conflicto",rechazo.Resultado);Assert.Contains("capacidad",rechazo.Detalle);
  Assert.Equal(JsonSerializer.Serialize(rechazo),JsonSerializer.Serialize(await Aplicar(client,hoja,comando)));
  Assert.All(await db.Bultos.AsNoTracking().Where(b=>b.EnvioId==retiroId).ToArrayAsync(),b=>Assert.Equal(EstadoCustodiaBulto.SinRecepcion,b.Custodia));
  Assert.False(await db.PruebasEntrega.AnyAsync(p=>p.EnvioId==retiroId));
  var descargar=await Aplicar(client,hoja,Operacion(Guid.NewGuid(),6,"EntregaBultos",entrega.EnvioId,0,new{ResultadosBultos=entrega.Bultos.Select(b=>new{BultoId=b.Id,Version=b.Version+1,Entregado=true}).ToArray()}));
  Assert.True(descargar.Resultado=="Aplicada",descargar.Detalle);
  var resolucion=Operacion(Guid.NewGuid(),7,"RetirarBultos",retiroId,retiro.EnvioVersion,new{ResuelveOperacionId=rechazo.Id,FotoId=foto,FirmaId=firma,ResultadosBultos=retiro.Bultos.Select(b=>new{BultoId=b.Id,b.Version,Entregado=true}).ToArray()});
  var hecho=await Aplicar(client,hoja,resolucion);Assert.True(hecho.Resultado=="Aplicada",hecho.Detalle);Assert.Equivalent(hecho,await Aplicar(client,hoja,resolucion));
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),8,"Rendicion",datos:new{BultoIds=retiro.Bultos.Select(b=>b.Id).ToArray()}))).Resultado);
  Assert.Equal("Aplicada",(await Aplicar(client,hoja,Operacion(Guid.NewGuid(),9,"FinJornada"))).Resultado);
  var e=await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId);
  using var recepcion=await client.PostAsJsonAsync($"/api/deposito/envios/{retiroId}/recepcion",new{e.Version});Assert.True(recepcion.StatusCode==HttpStatusCode.NoContent,await recepcion.Content.ReadAsStringAsync());
  e=await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId);Assert.Equal(EstadoEnvio.RetiroEnDeposito,e.Estado);
  using var final=await client.PostAsJsonAsync($"/api/deposito/retiros/{retiroId}/entrega-comercio",new{e.Version});Assert.Equal(HttpStatusCode.NoContent,final.StatusCode);
  Assert.Equal(EstadoEnvio.EntregadoComercio,(await db.Envios.AsNoTracking().SingleAsync(e=>e.Id==retiroId)).Estado);
  Assert.All(await db.Bultos.AsNoTracking().Where(b=>b.EnvioId==retiroId).ToArrayAsync(),b=>Assert.Equal(EstadoCustodiaBulto.Comercio,b.Custodia));
  Assert.Equal(cantidad,await db.PruebasEntrega.CountAsync(p=>p.EnvioId==retiroId)); Assert.All(await db.PruebasEntrega.Where(p=>p.EnvioId==retiroId).ToArrayAsync(),p=>Assert.Equal(foto,p.FotoId));
 }
}
