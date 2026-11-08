# Webhooks HTTP a comercios

Fuentes normativas: Laboratorio .NET 2026 §4, requisito 28, §6.8/§6.9 y Cambios Planificados 36/41. Esta funcionalidad agrega transporte HTTP; no reemplaza Kafka, su bandeja, el seguimiento público, reportes, tarifas ni liquidaciones.

## Suscripciones y permisos

Commerce `/webhooks` usa la identidad de comercio de la sesión validada. Cada suscripción pertenece a un comercio y operador inmutables. Lectura: `envios.consultar`. Configuración, rotación, revocación y reenvío: `configuracion.gestionar`. El operador con esos permisos puede consultar/reintentar entregas de su organización; la configuración del destino pertenece al comercio. RLS forzado aplica también si se manipulan IDs o filtros.

- `GET /api/webhooks/suscripciones?operadorId=…`
- `PUT /api/webhooks/suscripciones/{id}`: `{operadorId,url,eventos,contrato,habilitada,revision,secreto}`. Crear con un UUID nuevo y `revision:0`; editar con la revisión leída. `409` si otra sesión guardó antes. Una clave nueva es `whsec_` más Base64 de 32 bytes aleatorios. En edición, vacío conserva la clave.
- `POST /api/webhooks/suscripciones/{id}/revocaciones`: cuerpo JSON con el número de revisión. Deshabilita nuevos avisos, cancela los pendientes e invalida reservas; registra la revisión y actor. Un HTTP ya iniciado puede haber tenido efecto. Retirar también la clave del receptor. Reactivar exige clave nueva.
- `GET /api/webhooks/entregas?operadorId=…&estado=…&tipo=…&suscripcionId=…&pagina=1&tamano=25`: filtros, total y paginación en SQL (tamaño 1–100).
- `GET /api/webhooks/entregas/{id}?pagina=1`: snapshot, intentos y reenvíos (25 por página).
- `POST /api/webhooks/entregas/{id}/reenvios`: solo finalizadas (`Fallido`/`Entregado`). El segundo caso sirve para comprobar idempotencia. Siempre audita actor, fecha y estado anterior. No se reenvían avisos de revisiones revocadas, aunque luego se reactive con una clave nueva.

URL, eventos, versión, deshabilitación y rotación afectan **nuevos eventos**. Cada entrega existente conserva URL, revisión, contrato, bytes, clave cifrada y `ClaveId`. Editar una URL no redirige pendientes ni reenvíos. Deshabilitar permite drenar pendientes. Revocar los cancela. Al rotar se conserva la clave anterior en el receptor durante el drenaje y posibles reenvíos históricos. Para retirarla definitivamente, revocar la suscripción antigua y crear otra con una clave nueva.

Las claves se protegen con ASP.NET Core Data Protection, aplicación compartida API/worker existente y propósito exclusivo `NexoRuta.CommerceWebhooks.Secrets.v1`. API y worker HTTP necesitan el mismo key ring persistente protegido por los mecanismos de hosting. Los secretos, key rings y contraseñas no van a Git. Ningún listado o historial contiene el secreto ni su ciphertext. No habilitar sensitive-data logging de EF.

## Captura y garantía real

Se reutiliza la outbox canónica `EventoOperativo`, originada en eventos confirmados de envío, resultado de bulto y visitas. Un trigger diferido de la misma transacción materializa `WebhookDeliveries` por suscripción; permite ver los bultos finales de la admisión sin publicar antes del commit. Conserva estado/versión del hecho canónico, no reconstruye el estado histórico desde el actual al reintentar. Si la transacción hace rollback, tampoco quedan evento, trabajo HTTP ni marcador de visita. `WebhookVisitFacts` impide anunciar otra visita por cada actualización de estimaciones de la misma parada; una nueva ruta conserva una identidad de visita nueva.

La tabla de entregas es la outbox durable del transporte HTTP, independiente de que Kafka esté disponible o haya consumido el evento. Su identidad única `(EventoId,SuscripcionId)` evita trabajos duplicados. Los snapshots, historial de intentos y auditoría no se borran automáticamente. Los consumidores Kafka y su formato siguen intactos.

El worker `Messaging:Transport=HttpWebhooks` obtiene reservas con `FOR UPDATE SKIP LOCKED`, tokens por intento y lease de 90 segundos. Cada intento se persiste **antes** del HTTP. Finalizar exige token vigente. La reserva vencida conserva el intento como resultado desconocido y permite recuperación por otro worker. Reiniciar no pierde pendientes. No se retiene una transacción SQL abierta mientras viaja el HTTP.

Estados: `Pendiente`, `Procesando`, `Reintento`, `Entregado`, `Fallido`, `Cancelado`. Cualquier `2xx` confirma recepción. Red, timeout, `408`, `425`, `429` y `5xx` se reintentan; otros `4xx` y redirecciones terminan como fallidos. `Retry-After` se respeta hasta el límite configurado. No se leen ni almacenan cuerpos de error. El diagnóstico es un código/frase controlado de hasta 120 caracteres. Agotamiento y fallos permanentes permanecen consultables.

Configuración worker:

| Clave | Predeterminado | Límite |
|---|---:|---:|
| `Webhooks:MaxAttempts` | 6 | 1–20, capturado al primer intento |
| `Webhooks:TimeoutSeconds` | 15 | 1–30 |
| `Webhooks:RetryBaseSeconds` | 10 | ≥1 |
| `Webhooks:RetryMaxSeconds` | 3600 | base–86400 |

Espera: `min(tope, base × 2^(intentoDelCiclo−1))`. El reenvío inicia otro ciclo sin resetear el contador total ni cambiar la identidad del evento. Las entregas tienen orden de agenda, **sin promesa de orden global** entre workers o entre reintentos. Los consumidores deben usar fecha, versión e identidad para sus proyecciones.

Garantía: intentos **al menos una vez hasta el límite configurado**, con fallidos durables y recuperación manual. No se promete recepción eventual si el destino nunca vuelve. Si se pierde una respuesta después del commit remoto habrá un duplicado. El receptor debe persistir la identidad y su efecto en la misma transacción. Reenviar jamás modifica estado, custodia, tarifas, cuenta o liquidación del envío.

## Contratos

Tipos v2: `EnvioCreado`, `OperacionActualizada` (incluye entrega parcial), `BultoActualizado`, `NuevaVisita`, `RetiroSolicitado`, `RetiroRetirado`, `RetiroEntregadoComercio`. Las transiciones intermedias del retiro también aparecen como `OperacionActualizada` con `esRetiro:true`.

Antes de esta implementación no había un contrato de **transporte HTTP**. Existía el mensaje Kafka `EnvioCreado` v1 con cuatro campos y los avisos Kafka de bulto/operación v2. En cumplimiento explícito del cambio 36, se conservan esos productores/consumidores sin alterar sus payloads. La suscripción HTTP v1 conserva la forma anterior de estado de envío; los eventos nuevos y el detalle de bultos requieren v2. No se agregan aliases de APIs ni compatibilidad ajena a esta obligación.

v1 (identidad en `webhook-id`, contrato y tipo en headers):

```json
{"envioId":"019a0000-0000-7000-8000-000000000001","operadorId":"019a0000-0000-7000-8000-000000000002","comercioId":"019a0000-0000-7000-8000-000000000003","estado":"Entregado"}
```

v2 (ejemplo ilustrativo de forma; ver payloads sintéticos efectivamente transmitidos en `docs/webhooks/examples.json`):

```json
{"schemaVersion":2,"eventId":"019a0000-0000-7000-8000-000000000004","tipo":"OperacionActualizada","ocurridaEnUtc":"2026-11-08T02:00:00Z","operadorId":"019a0000-0000-7000-8000-000000000002","comercioId":"019a0000-0000-7000-8000-000000000003","envioId":"019a0000-0000-7000-8000-000000000001","estado":"EntregadoParcialmente","versionOperacion":8,"esRetiro":false,"progreso":{"total":3,"entregados":2,"pendientes":1},"bulto":null,"visita":null}
```

`envioId` es la referencia autorizada que devuelve el alta autenticada; no es el token de seguimiento público. `bulto` contiene ID/código autorizado, resultado, custodia y versión; `visita` ruta y orden. No se incluyen destinatario, dirección, foto/firma personal, teléfono, correo, token público, credenciales ni datos de otro comercio. Fechas del hecho son UTC y se distinguen de recepción e intento. `eventId` es estable entre contratos y reintentos. Un receptor que usa dos suscripciones de distinto contrato debería usar destinos/bandejas independientes: el mismo evento con distinto cuerpo y la misma identidad provoca `409` en el receptor de ensayo.

## Firma: Standard Webhooks

Especificación oficial consultada: https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md (secciones Signature scheme, Headers, Verification). API criptográfica oficial .NET: https://learn.microsoft.com/dotnet/api/system.security.cryptography.hmacsha256 y https://learn.microsoft.com/dotnet/api/system.security.cryptography.cryptographicoperations.fixedtimeequals.

Headers: `webhook-id` (UUID del evento), `webhook-timestamp` (segundos Unix del **intento**) y `webhook-signature` (`v1,BASE64`). Entrada exacta: UTF-8 de `id + "." + timestamp + "."` seguido por **los bytes del body transmitido**, sin parsear ni volver a serializar. HMAC-SHA256 usa los 32 bytes decodificados después del prefijo `whsec_`. El receptor compara en tiempo constante y exige distancia temporal ≤300 segundos en ambas direcciones. Cada reintento conserva ID y body, pero vuelve a fechar y firmar. Sin reloj sincronizado las solicitudes serán rechazadas.

`NexoRuta-Contract-Version`, `NexoRuta-Event-Type`, `NexoRuta-Key-Id` y `NexoRuta-Delivery-Id` ayudan al diagnóstico. Los headers adicionales no forman parte de la firma estándar. El receptor deriva tipo/versión del cuerpo autenticado y los contrasta con los headers; `Key-Id` no decide la autorización. Acepta claves vigentes y anteriores configuradas en el receptor, y la lista de firmas estándar. Una firma válida no evita por sí sola un replay dentro de 5 minutos: la deduplicación durable por `webhook-id` evita el segundo efecto.

Verificador mínimo Python (cuerpo leído como bytes, claves por canal seguro):

```python
import base64, hashlib, hmac, time
assert abs(int(time.time()) - int(timestamp)) <= 300
expected = hmac.new(base64.b64decode(secret[6:]),
    (event_id + '.' + timestamp + '.').encode() + body, hashlib.sha256).digest()
assert any(part.startswith('v1,') and hmac.compare_digest(expected,
    base64.b64decode(part[3:])) for part in signature.split())
# Luego validar contrato e insertar identidad + efecto con unicidad transaccional.
```

## Destinos y exposición

Producción: HTTPS y origen exacto autorizado en `Webhooks:AllowedOrigins` tanto en API como worker (ejemplo `https://merchant.example.test`). Predeterminado deniega destinos. No se permiten userinfo, query, fragmento ni redirecciones. El worker revalida snapshots contra la política vigente y resuelve DNS al conectar, rechaza IPs privadas/reservadas y conecta a la IP validada; no confía en otra resolución posterior ni en proxies heredados que pudieran eludir ese control. Es un transporte de salida con conexión directa y debe acompañarse de reglas de egress del despliegue.

Solo Development/Testing admite `Webhooks:DevelopmentReceiverUrl`, una URL **exacta** terminada en `/comercio/webhook`; permite HTTP/dirección privada únicamente a ese receptor. No cambia las reglas globales. En Compose es `http://tracking:8080/comercio/webhook`; en procesos Cloud es `http://127.0.0.1:5103/comercio/webhook`. El navegador abre `http://localhost:5103/comercio`.

El receptor se habilita con `CommerceDemo:Enabled=true`, falla al arrancar si se intenta habilitar en producción y devuelve 404 si está deshabilitado. Requiere contraseña de controles de al menos 24 caracteres, claves de firma y ruta de SQLite propia. El panel y los controles requieren cookie de ensayo de cuatro horas y antiforgery; el endpoint HTTP usa firma en lugar de cookie/antiforgery. Límite 64 KiB por body, depth 16, respuesta pequeña, sin cuerpos inválidos guardados. SQLite WAL con transacción identidad+efecto+intento, máximo 256 MiB/10000 eventos; conserva IDs durante todo el ensayo y últimos 2000 intentos. La capacidad agotada falla explícitamente, nunca borra IDs para aparentar deduplicación. Su volumen/key ring deben persistir entre reinicios. No tiene conexión a la base de NexoRuta ni usa su API para poblar la bandeja.
