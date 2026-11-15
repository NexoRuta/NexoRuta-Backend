# Reintento administrativo de publicaciones internas

Backoffice `/mensajes-internos` reutiliza la sesión del operador. Tanto GET
`/api/admin/mensajes-internos?pagina=1&tamano=25` como POST
`/api/admin/mensajes-internos/{id}/reintentos` requieren
`configuracion.gestionar` y `envios.consultar`. No admite comercios ni plataforma;
no concede a Admin acceso operativo. PostgreSQL revalida actor, acceso activo,
organización activa y permisos persistidos. RLS limita cada lectura/escritura al
operador, con un propietario de funciones sin login ni bypass RLS. API no recibe
SELECT/UPDATE directo sobre el outbox.

La selección comprende publicaciones RabbitMQ/Kafka en `Error`, sin reserva,
sin recepción procesada. Los mensajes de identidad no pertenecen al operador.
Push conserva su mecanismo específico de despacho/backoff/recibos. Se muestra
ID, tipo, destino, estado, intentos y un diagnóstico fijo seguro; nunca payload,
credenciales ni el texto original de excepciones.

POST recibe `{ solicitudId, intentos, motivo }`. El motivo no vacío, de hasta
500 caracteres, explica la intervención. Se bloquea la identidad de solicitud y
luego la fila del mensaje: una solicitud repetida devuelve su resultado original;
otra solicitud concurrente solo puede conseguir la transición una vez. La
cantidad esperada de intentos evita reintentar desde una pantalla anterior a un
nuevo fallo. El resultado es `Solicitado`, `NoElegible` o `NoDisponible`; este
último no distingue inexistencia de otro ámbito. Estos resultados de negocio
usan HTTP 200 para confirmar también la auditoría de rechazos dentro de la
transacción RLS. Autorización e input inválido mantienen HTTP 401/403/400.

`OutboxRetryRequests` es un historial inmutable: solicitud, operador, actor y
acceso, mensaje seleccionado, fecha UTC del servidor, intentos esperados, motivo,
estado previo y resultado. No copia el payload ni borra el diagnóstico del
outbox. La operación cambia exclusivamente `Estado` a `Pendiente` y adelanta
`ProximoIntentoEnUtc`. Conserva ID, contrato, payload, prioridad y contador total.
El Worker habitual reclama con lease y `SKIP LOCKED`, publica y espera confirmación
normal; el consumidor conserva sus recibos/idempotencia por MessageId.

## Publicación y consumo son estados distintos

`OutboxMessages.Publicado` significa confirmación de publicación, no consumo
exitoso. Nunca se lo resetea para simular recuperación de consumo. Seguimiento
registra ese fallo en RabbitMQ con los contadores y `x-nexoruta-error-kind`:
transitorios van a reintento; errores de procesamiento agotados/contrato van a
fallidos. Su recuperación selectiva mantiene el procedimiento de
[DLQ](seguimiento-rabbitmq-dlq.md), incluida confirmación antes del ACK y auditoría
manual. El reenvío HTTP de webhooks mantiene su API y sus snapshots propios.

## Verificación

`AdministrativeOutboxRetryTests` usa PostgreSQL18 y un broker RabbitMQ4
Testcontainers aislados. Prueba roles restringidos, autorización, aislamiento,
solicitudes repetidas/concurrentes, auditoría y exclusión de procesados, además
del Worker/publicador/consumidor reales y duplicado con el mismo MessageId.
La UI tiene pruebas HTTP para permisos y respuesta incierta y un recorrido
Chromium a 1440/320px con API controlada; esta última prueba no acredita el broker.
