# Publish EnvioCreado.v1 through the transactional outbox

Shipment creation already saves EnvioCreado and GenerarSeguimiento in the same EF
SaveChanges transaction. Kafka now dispatches the existing Kafka row; it does not
generate another event or alter the shipment payload/transaction. There is no
production Kafka consumer in this implementation.

## Event contract

| Element | Serialization |
| --- | --- |
| Topic | `nexoruta.envios.creados.v1` |
| Key | `OutboxMessage.EnvioId`, lowercase GUID `D`, UTF-8 string |
| Value | Existing `OutboxMessage.Payload` string, UTF-8 JSON, unchanged |
| Header `eventId` | Outbox row Id, lowercase GUID `D`, UTF-8 bytes |
| Header `eventType` | `EnvioCreado`, UTF-8 bytes |
| Header `eventVersion` | Decimal text `1`, UTF-8 bytes (not a binary integer) |
| Header `occurredAt` | Outbox `CreadoEnUtc`, UTC, invariant round-trip `O` format, UTF-8 bytes |
| Header `operadorId` | Operator context, lowercase GUID `D`, UTF-8 bytes |
| Header `contentType` | `application/json`, UTF-8 bytes |

The unchanged value has `envioId`, `operadorId`, `comercioId` (GUID strings) and
`estado` (currently `Admitido`). OccurredAt is the original creation time, not the
publication/retry time or the Kafka record timestamp. PostgreSQL's persisted time
precision is authoritative. EventId, key, value and headers remain stable on replay.
The key gives partition affinity while the topic's partition layout is stable;
it does not establish global ordering or deduplicate business events.

This is an internal business event, not the public tracking HTTP contract. Tenant
headers convey context, not proof of authorization: future consumers must validate
the trusted source and enforce their own tenant boundary.

## Two independent host processes

Use the same Worker executable in **two processes**, not two transports in one
host. The existing RabbitMQ consumer intentionally stops its host after uncertain
transfer/ACK failures. Kafka must not share that shutdown boundary.

| Process | `Messaging:Transport` | Registered services |
| --- | --- | --- |
| Existing RabbitMQ worker | `RabbitMq` (default) | Rabbit outbox Worker and tracking consumer |
| Kafka dispatcher | `Kafka` | KafkaOutboxWorker and KafkaEnvioCreadoPublisher only |

Both use the existing PostgreSQL connection configuration. Kafka mode does not
require RabbitMQ credentials or the tracking AES key. A Kafka outage does not stop
the RabbitMQ process, and vice versa. Provisioning/startup supervision of the second
process is an infrastructure follow-up: Compose has **not** been changed.

After supplying connection/security configuration externally, select the Kafka
process with a command-line override (do not put secrets on the command line):

```powershell
dotnet run --project src/NexoRuta.Worker --configuration Release --no-launch-profile -- --Messaging:Transport=Kafka
```

## Kafka configuration

Environment variables use `__` in place of `:`. No broker address or credential is
committed in a configuration file by this feature.

| Setting | Behavior |
| --- | --- |
| `Kafka:BootstrapServers` | Required, supplied by the environment |
| `Kafka:MessageTimeoutMs` | Default 30000; accepted 1–60000 ms, below the two-minute SQL lease |
| `Kafka:SecurityProtocol` | Optional Confluent enum; configure explicitly for production |
| `Kafka:SaslMechanism` | Optional Confluent enum |
| `Kafka:SaslUsername`, `Kafka:SaslPassword` | Optional external secrets |
| `Kafka:SslCaLocation` | Optional external CA certificate path |

Idempotence is always enabled and Acks is always All. The producer is reused for
the host lifetime; native logs are suppressed in favor of structured codes, IDs,
partition/offset and delivery status. Payloads, exception reasons, credentials and
tokens are not logged by these components. Creation is lazy: missing Kafka settings
do not affect the default RabbitMQ host. Fix configuration and restart the Kafka
host if initialization fails or the native producer enters a fatal state.

The application does not provision topics. Before starting Kafka mode, provision
the topic, ACLs, TLS/SASL, replication factor, min.insync.replicas and retention.
Testcontainers uses a single KRaft broker with replication factor 1 only for tests;
that does **not** validate production durability or high availability.

## Delivery and restart semantics

1. Select due Pendiente/Error rows or expired Procesando leases for the requested
   destination. RabbitMQ and Kafka never reserve each other's rows.
2. A conditional SQL UPDATE gives exactly one dispatcher the active reservation.
   The lease lasts two minutes; the attempt counter saturates at Int32.MaxValue.
3. Publish the persisted event. ProduceAsync must return a delivery report with
   PersistenceStatus.Persisted before the row can become Publicado.
4. Complete only when the processor still owns a non-expired lease. A rejected
   completion is logged and leaves the row recoverable.
5. Delivery errors store a sanitized code/type, release the lease and schedule the
   next attempt 30 seconds later. SQL/host failure leaves the lease to expire.

PostgreSQL–Kafka delivery is **at-least-once**, not exactly-once. Idempotent Kafka
production protects native producer retries, not replay across an Outbox retry or
host restart. Kafka may accept an event before its confirmation is lost, before SQL
completion fails, or after cancellation of ProduceAsync. Cancellation does not
retract an in-flight Kafka record. Shutdown flush is bounded to five seconds, then
native resources are disposed; unfinished SQL leases remain recoverable.

Tests deliberately fail SQL completion after confirmed Kafka delivery and restart
the dispatcher: two different offsets carry the same EventId. Future consumers
need durable EventId-based idempotency. Never generate a replacement EventId merely
to replay a row. Transport downtime affects publication, not the atomic shipment
creation transaction; a database outage is a shared dependency of both transports.

## Diagnose pending or failed events safely

Inspect metadata, not payloads/credentials:

```sql
SELECT "Id", "Destino", "Tipo", "Estado", "Intentos", "CreadoEnUtc",
       "ProximoIntentoEnUtc", "ReservaHastaUtc", "ProcesadorId",
       "PublicadoEnUtc", "UltimoError"
FROM "OutboxMessages"
WHERE "Destino" = 'Kafka' AND "Estado" <> 'Publicado'
ORDER BY "CreadoEnUtc";
```

- Pendiente: verify that a Kafka-mode process is actually running and has config.
- Error: correlate EventId and sanitized Kafka code with broker/topic/ACL health;
  retries resume automatically after the deadline. Permanent errors also retry and
  require intervention; there is no automatic Kafka DLQ/discard policy here.
- Procesando: respect the active owner. After its crash, wait for lease expiration;
  a new dispatcher can reclaim the same row without changing identity or payload.
- Delivery confirmed but no Publicado: investigate PostgreSQL availability and
  lease ownership; assume a Kafka copy may already exist.

Do not delete/purge records, blindly mark them Publicado, alter payloads, or clear
active leases as a troubleshooting shortcut. An authorized manual replay must
first establish that the owner is stopped, preserve EventId and account for
duplicates. Broker security/replication, multi-node failures, prolonged outages,
capacity, supervision and real network-partition fault injection remain deployment
validation tasks. This feature alone does not make Kafka production-ready.

## Isolated validation

Docker Desktop must run Linux containers. Testcontainers provisions Kafka, RabbitMQ
and PostgreSQL with dynamic addresses and disposes only resources owned by tests.
No shared database migration or unfiltered legacy integration suite is required.

```powershell
dotnet build NexoRuta.sln --configuration Release
dotnet test tests/NexoRuta.UnitTests/NexoRuta.UnitTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento|FullyQualifiedName~Messaging'
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Kafka'
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento'
git diff --check
```

References: [Confluent .NET producer](https://docs.confluent.io/kafka-clients/dotnet/current/overview.html),
[delivery reports and cancellation](https://docs.confluent.io/platform/current/clients/confluent-kafka-dotnet/_site/api/Confluent.Kafka.IProducer-2.html).
