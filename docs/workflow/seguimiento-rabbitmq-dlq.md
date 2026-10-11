# Safely handle tracking retries and dead letters

The consumer ACKs an original delivery only after PostgreSQL commits or a
mandatory, persistent republication receives a publisher confirm. Failures to
publish/confirm/ACK stop the host: channel disposal releases unacknowledged
deliveries. A confirm or ACK lost in transit can cause duplicates, not exactly-once
delivery. Preserve MessageId: processing and the encrypted initial delivery remain
transactional and idempotent.

## Routing and counters

| Error kind | Destination | Processing budget |
| --- | --- | --- |
| `invalid` | `nexoruta.seguimiento.fallidos` immediately | No processing retry |
| `transient` | `nexoruta.seguimiento.reintento` | Unchanged |
| `processing` | Retry, then failed queue on third failure | Three failures total |

Transient means `TimeoutException`, Npgsql's concrete `IsTransient` classification,
or an EF `DbUpdateException` / `InvalidOperationException` wrapping either.
The latter covers Npgsql's default EF execution strategy wrapper, without relying
on localized exception text. Generic exceptions, generic
`DbUpdateException`, constraint violations and authentication errors are **not**
automatically transient. Host cancellation leaves the delivery unacknowledged.
Contract errors are identified during envelope/payload validation, not by treating
all JSON/data exceptions inside processing as malformed input.

- Retry queue TTL remains **30 seconds**; its dead-letter route is the main queue.
- `x-nexoruta-retry-count` counts scheduled additional processing, including
  transient retries. Initial processing is zero. DLQ transfer does not increment
  it. Increment saturates at `Int32.MaxValue`.
- `x-nexoruta-processing-failure-count` counts non-transient processing failures;
  transient failures preserve it. Legacy messages without this header use their
  prior retry count as the previous processing-failure budget.
- `x-nexoruta-error-kind` identifies the latest failure. Body, MessageId, Type,
  CorrelationId and existing headers are preserved when transferring messages.
- Logs contain identifiers, counters, destination, error type and SQLSTATE; not
  payloads, exception text, tokens, encryption keys or connection credentials.

## Inspect first

1. Verify environment/vhost, queue and permissions. Do not purge queues or use
   operational infrastructure to test recovery procedures.
2. Inspect queue statistics and structured logs by MessageId/error kind. Reading
   statistics does not consume messages. Do not export full payloads into logs.
3. If body inspection is necessary, use a bounded, authorized manual-ACK reader
   and requeue without acknowledging deletion. Management UI "Get messages" can
   consume messages: select a requeue mode explicitly. Even requeue inspection can
   change ordering/redelivery metadata. Protect any captured payload as sensitive.
4. Fix the root cause before replaying: schema/identity, missing shipment,
   database availability, credentials or application defect. Transient failures
   retry indefinitely, so persistent outages require intervention.

## Recover or discard one selected message

For recovery, read with **autoAck=false**. Validate the message against the
persisted shipment/tenant and check `(MessageId, Consumer)` in ProcessedMessages.
Republish its original identity and body as persistent with `mandatory=true` and
publisher confirms. ACK the DLQ delivery **only after destination confirmation**.
On timeout/failure, leave it unacknowledged and close the reader's channel.

An authorized replay may reset the two counters to zero after the cause is fixed;
record that administrative decision and the old counters separately. Otherwise a
spent processing budget will send the message straight back to DLQ on another
processing failure. Never invent a new MessageId to bypass idempotency. Correcting
a malformed identity/body requires explicit reconciliation, not a blind replay.

If confirmation is lost, both queues may contain a copy. Existing idempotency
protects repeated processing of the same valid message, but is not authorization
and does not make altered payloads safe. Replaying already-processed messages
does not regenerate expired links or resend notifications.

Discard only after an authorized decision and a minimal audit record
(MessageId/cause/action, without secrets); ACK exactly the selected delivery.
Never purge an entire DLQ as an inspection or recovery shortcut.

## Remaining reliability boundaries

- Automatic recovery does not retry the initial connection attempt. A failed
  start requires the host/service supervisor to restart it.
- Publisher confirms protect client publications, not the retry queue's internal
  TTL dead-letter hop. Existing classic queues do not provide quorum at-least-once
  dead-lettering. Do not change queue type/arguments on deployed queues in place.
- Recovery, confirm loss and ACK loss during real network partitions still need
  dedicated fault-injection validation. There is no exactly-once broker guarantee.

See [RabbitMQ .NET recovery](https://www.rabbitmq.com/client-libraries/dotnet-api-guide)
and [dead-letter safety](https://www.rabbitmq.com/docs/dlx#safety).

## Safe automated checks

Use Docker Desktop and the filtered Testcontainers suites; never the legacy
unfiltered integration suite with its external PostgreSQL fallback:

```powershell
dotnet build NexoRuta.sln --configuration Release
dotnet test tests/NexoRuta.UnitTests/NexoRuta.UnitTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento'
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --configuration Release --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento'
git diff --check
```
