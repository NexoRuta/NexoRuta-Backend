using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NexoRuta.Infrastructure.Persistence;
using NexoRuta.IntegrationTests.Fixtures;
using NexoRuta.Worker.Messaging;
namespace NexoRuta.IntegrationTests.Gestion;
public sealed class GestionWorkerTests(PostgresFixture postgres,KafkaFixture kafka):IClassFixture<PostgresFixture>,IClassFixture<KafkaFixture>
{
    [Fact]
    public async Task KafkaRepetidoFueraDeOrdenYReconstruccionConRolWorkerNoRepiteEfectosEconomicos()
    {
        var s=await GestionScenario.CreateAsync(postgres);var topic="management-"+Guid.NewGuid().ToString("N");var group=topic+"-consumer";
        using var admin=new AdminClientBuilder(new AdminClientConfig{BootstrapServers=kafka.BootstrapServers}).Build();
        await admin.CreateTopicsAsync([new TopicSpecification{Name=topic,NumPartitions=1,ReplicationFactor=1}]);
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{
            ["Kafka:BootstrapServers"]=kafka.BootstrapServers,["Operational:Topic"]=topic,["Operational:Partitions"]="1",["Operational:nexoruta-management-v1:Group"]=group
        }).Build();
        var services=new ServiceCollection();services.AddDbContext<NexoRutaDbContext>(o=>o.UseNexoRutaPostgres(s.Worker.ConnectionString));await using var provider=services.BuildServiceProvider();
        var consumer=new ManagementReportingConsumer(config,provider.GetRequiredService<IServiceScopeFactory>(),NullLogger<ManagementReportingConsumer>.Instance);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await consumer.StartAsync(timeout.Token);
        try{
            await using var db=s.Data.CreateDbContext();
            var events=await db.OutboxMessages.Where(x=>x.Tipo=="EventoOperativo").OrderByDescending(x=>x.CreadoEnUtc).Select(x=>x.Payload).ToArrayAsync();
            using var producer=new ProducerBuilder<string,string>(new ProducerConfig{BootstrapServers=kafka.BootstrapServers,Acks=Acks.All}).Build();
            foreach(var payload in events.Concat(events.Take(5)))
            {
                using var json=System.Text.Json.JsonDocument.Parse(payload);var e=json.RootElement;
                await producer.ProduceAsync(topic,new(){Key=e.GetProperty("operadorId").GetString()+":"+e.GetProperty("entidadId").GetString(),Value=payload},timeout.Token);
            }
            async Task CatchUp()
            {
                using var probe=new ConsumerBuilder<string,string>(new ConsumerConfig{BootstrapServers=kafka.BootstrapServers,GroupId=group,EnableAutoCommit=false}).Build();
                var target=events.Length+5;
                while(!timeout.IsCancellationRequested){
                    var count=await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"GestionPendientes\" WHERE \"Generacion\"<>\"Aplicada\"").SingleAsync(timeout.Token);
                    var offset=probe.Committed([new TopicPartition(topic,0)],TimeSpan.FromSeconds(2)).Single().Offset.Value;
                    if(count==0 && offset>=target)return;
                    await Task.Delay(100,timeout.Token);
                }
                throw new TimeoutException("The management consumer did not reconcile its owned facts.");
            }
            await CatchUp();
            const string snapshotSql="""
                SELECT jsonb_build_object('operations',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g."EnvioId") FROM public."GestionOperaciones" g),
                 'parcels',(SELECT jsonb_agg(to_jsonb(b) ORDER BY b."BultoId") FROM public."GestionBultos" b),
                 'reasons',(SELECT jsonb_agg(to_jsonb(m) ORDER BY m."OperacionId",m."Tipo",m."Motivo") FROM public."GestionMotivos" m),
                 'money',(SELECT sum("ImporteUyu") FROM public."MovimientosCuenta"))::text AS "Value"
                """;
            var before=await db.Database.SqlQueryRaw<string>(snapshotSql).SingleAsync();
            await using(var worker=s.Worker.CreateDbContext()){
                await worker.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.request_management_rebuild({s.OperatorA.OperadorId})");
                await worker.Database.ExecuteSqlInterpolatedAsync($"SELECT nexoruta_app.request_management_rebuild({s.OperatorB.OperadorId})");
            }
            await CatchUp();Assert.Equal(before,await db.Database.SqlQueryRaw<string>(snapshotSql).SingleAsync());
            Assert.Equal(0,await s.ReconcileAsync());
            Assert.Equal(0,await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.\"CargosLiquidados\"").SingleAsync());
        }finally{await consumer.StopAsync(CancellationToken.None);consumer.Dispose();}
    }
}
