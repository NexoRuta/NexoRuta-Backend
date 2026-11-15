# ADR — Consistencia transaccional y Outbox

**Estado**. Decisión implementada para la admisión de envíos, la publicación asíncrona y los consumidores descritos abajo. El cierre diario automático y el reintento administrativo de mensajes internos siguen pendientes.

## Contexto y decisión

La admisión del envío y la entrega de un mensaje al broker o a un comercio ocurren en sistemas distintos. Si se envía el mensaje antes del commit de PostgreSQL, el receptor puede actuar sobre un envío que termine revertido. Si se publica después del commit sin guardar antes el trabajo, una caída del proceso puede dejar el envío sin mensaje.

PostgreSQL guarda los cambios de negocio y las filas de Outbox en una sola transacción. El Worker publica después del commit, reserva el trabajo por destino y conserva la identidad del mensaje entre intentos. Los consumidores deduplican sus efectos de forma durable. La confirmación del broker, la confirmación del consumidor y la entrega HTTP son eventos distintos.

## Qué se confirma junto

- **Alta individual**. La transacción de admisión guarda envío, destinatario, dirección, bultos, tarifa aplicada, confirmación inicial del destino y filas de `OutboxMessages` para `GenerarSeguimiento` (RabbitMQ) y `EnvioCreado` v1 (Kafka). El rollback revierte los datos del envío y el trabajo de publicación.
- **Alta por CSV**. El lote aplica la misma transacción a sus altas nuevas y guarda además las identidades de importación, con comercio, referencia externa, operador y huella versionada. Una reimportación equivalente reutiliza el envío original sin insertar nuevas filas de Outbox. Un rechazo atómico revierte las escrituras del lote.
- **Hechos operativos con webhooks**. Para un hecho canónico `EventoOperativo` elegible, un trigger diferido del Outbox registra `WebhookDeliveries` en la transacción de origen. Cada entrega conserva la revisión de suscripción, URL, contrato, payload y secreto protegido vigentes para ese hecho. El rollback elimina también ese trabajo HTTP.

[Persistencia de envíos](../../src/NexoRuta.Infrastructure/Envios/Repositories/EfEnviosRepository.cs) · [Importación CSV](../cu/CU-07-alta-envios.md) · [ADR de webhooks](002-http-commerce-webhooks.md)

## Publicación y consumo internos

1. **Reserva y publicación**. RabbitMQ y Kafka reservan filas de su propio destino con una concesión temporal en PostgreSQL. Tras un fallo de publicación se registra el error y se programa un nuevo intento. Si el proceso cae, otra instancia puede recuperar la fila al vencer la reserva. El Worker usa el mismo ID y payload al volver a publicar.
2. **Confirmación del publicador**. El Worker marca `Publicado` únicamente tras el *publisher confirm* de RabbitMQ o el informe `PersistenceStatus.Persisted` de Kafka, y solo mientras conserva una reserva vigente. `Publicado` registra la aceptación del broker. El resultado del consumidor se determina por separado.
3. **ACK y deduplicación de seguimiento**. El consumidor RabbitMQ verifica el trabajo persistido, guarda el enlace y su entrega inicial cifrada junto con `ProcessedMessages`, y confirma la transacción antes de enviar el ACK. La deduplicación técnica usa `(MessageId, Consumer)`. La comprobación por operador y envío protege además la entrega inicial frente a un segundo mensaje válido para el mismo envío.
4. **Deduplicación de la bandeja de comercio en Kafka**. Su consumidor verifica la identidad del aviso contra el evento persistido, inserta por `EventoId` de forma idempotente y confirma el *offset* después de que PostgreSQL acepte la operación. Los consumidores de `EnvioCreado` v1 deben usar el `eventId` estable como identidad del procesamiento.

Puede haber una nueva entrega si el broker aceptó el mensaje pero se perdió su confirmación, si falló el marcado en PostgreSQL o si el consumidor confirmó su transacción y se perdió el ACK. El diseño admite repetición y sus efectos dependen de la idempotencia del consumidor. La idempotencia del productor Kafka cubre sus reintentos internos. Una nueva publicación de Outbox puede producir otro registro con el mismo `eventId`.

[Publicación RabbitMQ](../../src/NexoRuta.Worker/Worker.cs) · [Contrato Kafka de alta](../workflow/envio-creado-kafka.md#event-contract) · [Procesamiento de seguimiento](../../src/NexoRuta.Infrastructure/Seguimiento/Processing/ProcesarGeneracionSeguimiento.cs) · [Consumidor de avisos Kafka](../../src/NexoRuta.Worker/Messaging/CommerceNoticeConsumer.cs)

## Recuperación de fallidos

| Dónde falló | Recuperación vigente |
| --- | --- |
| Publicación desde `OutboxMessages` | Los errores de publicación RabbitMQ/Kafka se reintentan al llegar el próximo intento programado. Una reserva vencida puede reclamarse. Para un fallo persistente se diagnostica por ID de mensaje, destino y error antes de corregir la causa. |
| Consumo RabbitMQ de `GenerarSeguimiento` | Un error transitorio vuelve a la cola de reintento con espera de 30 segundos y conserva el presupuesto de fallos de procesamiento. Un mensaje inválido pasa a fallidos de inmediato. Al tercer fallo de procesamiento pasa a `nexoruta.seguimiento.fallidos`. En cada traslado se envía el ACK del original después de que RabbitMQ confirme la republicación. |
| Cola de fallidos de seguimiento | Existe un procedimiento operativo manual. Tras corregir la causa, se valida el trabajo persistido, se republica el mensaje seleccionado con su ID y payload originales, y se confirma el original de la cola de fallidos después de recibir el *publisher confirm*. |
| Entrega HTTP a un comercio | El Worker `HttpWebhooks` trabaja sobre `WebhookDeliveries`, sin depender de los brokers. Persiste el intento antes del HTTP y marca `Entregado` ante `2xx` con reserva vigente. Reintenta fallos recuperables con espera creciente y un límite. Los fallos permanentes o agotados quedan consultables para reenvío autorizado y auditado. |

El trabajo HTTP es único por `(EventoId, SuscripcionId)`. Los reenvíos conservan la identidad y el payload. Si el receptor guardó el efecto y su respuesta se perdió, puede recibirlo nuevamente. Debe guardar `webhook-id` y el efecto en su propia transacción. El reenvío de un webhook opera solo sobre esa entrega HTTP.

[Procedimiento para fallidos RabbitMQ](../workflow/seguimiento-rabbitmq-dlq.md#recover-or-discard-one-selected-message) · [Contrato HTTP, reintentos y reenvíos](../webhooks/http.md)

## Pendientes de implementación

- **Cierre diario automático (AP-09)**. Falta ejecutar el cierre diario mediante el Worker. El comando `FinJornada` y el barrido de plazos de validación de evidencias son flujos existentes con otros disparadores. [Alcance funcional](../workflow/alcance-funcional.md) · [Cierre de jornada](../cu/cierre-jornada.md)
- **Reintento administrativo de mensajes internos**. Falta una operación administrativa de la aplicación para seleccionar y relanzar mensajes internos fallidos con autorización y auditoría. El procedimiento manual de RabbitMQ y el reenvío HTTP de webhooks son mecanismos vigentes con sus propios alcances.

## Evidencia automatizada

- [ShipmentCsvImportHttpTests](../../tests/NexoRuta.IntegrationTests/Envios/ShipmentCsvImportHttpTests.cs) cubre unicidad, respuesta perdida tras el commit y rollback de envíos, identidad y Outbox.
- [KafkaOutboxTests](../../tests/NexoRuta.IntegrationTests/Messaging/KafkaOutboxTests.cs) y [KafkaOutboxReservationTests](../../tests/NexoRuta.IntegrationTests/Messaging/KafkaOutboxReservationTests.cs) cubren confirmación, reservas y republicación con el mismo ID.
- [GenerarSeguimientoConsumerExitoTests](../../tests/NexoRuta.IntegrationTests/Seguimiento/Seguimiento/GenerarSeguimientoConsumerExitoTests.cs) y [GenerarSeguimientoConsumerTests](../../tests/NexoRuta.IntegrationTests/Seguimiento/GenerarSeguimientoConsumerTests.cs) cubren redelivery, deduplicación, ACK y traslado a reintento o fallidos.
- [WebhooksHttpTests](../../tests/NexoRuta.IntegrationTests/Webhooks/WebhooksHttpTests.cs) y [WebhooksFullStackTests](../../tests/NexoRuta.IntegrationTests/Webhooks/WebhooksFullStackTests.cs) cubren captura transaccional, snapshots, reservas, agotamiento y reenvío.
