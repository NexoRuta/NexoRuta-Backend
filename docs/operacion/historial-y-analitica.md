# Historial y analítica operativa

La API presenta la proyección de lectura `operational_history_v1`, derivada de los eventos canónicos capturados en PostgreSQL y publicados por el worker Kafka existente. PostgreSQL continúa siendo la fuente de verdad. MongoDB no participa en la aceptación de un envío ni sirve de fallback para consultas transaccionales.

## Endpoints

Ambos endpoints requieren una sesión válida con el permiso `envios.consultar` y respuestas `Cache-Control: no-store`.

### `GET /api/operacion/historial`

Parámetros requeridos: `operadorId`, `desde`, `hasta`. Opcionales: `envioId`, `tipo`, `offset` (predeterminado 0) y `limite` (predeterminado 50).

- Fechas ISO 8601 con zona horaria; el servidor normaliza a UTC y consulta el intervalo `[desde, hasta)` por `registradaEnUtc`.
- El intervalo máximo es 31 días; `limite` acepta 1–100 y `offset` 0–10000.
- El tipo debe pertenecer a la allowlist operativa. La API nunca recibe expresiones MongoDB.
- El operador debe coincidir con la sesión del operador. Para comercio, el `ComercioId` siempre proviene de la sesión y se agrega al filtro documental.
- Orden: `registradaEnUtc DESC, _id DESC`. La paginación por offset es viva: inserciones entre solicitudes pueden desplazar filas.
- Respuesta: `estadoProyeccion`, `verificadaEnUtc`, `retencionDias`, `offset`, `limite` y `datos` con el envelope `EventoOperativo`.

### `GET /api/operacion/analitica/volumen`

Parámetros: `operadorId`, `desde`, `hasta`. Comparte autorización y rango temporal con el historial. `volumenPorTipo` cuenta **eventos** dentro del intervalo; no representa envíos únicos, bultos, dinero ni actividad empresarial deduplicada por entidad. El pipeline filtra ámbito, fecha y expiración en MongoDB antes de agrupar por tipo. Un rango sin eventos devuelve un mapa vacío.

### Estados y errores

`estadoProyeccion` puede ser `Actualizada`, `Atrasada` o `Indisponible`. Se comprueba la salud antes y después de consultar MongoDB; `Actualizada` requiere lecturas actuales y una frontera estable. `verificadaEnUtc` indica el momento de lectura y no promete una transacción distribuida entre PostgreSQL y MongoDB.

- `400`: parámetros ausentes o fuera de los límites.
- `401`: sesión ausente o inválida, según el middleware de autenticación.
- `403`: permiso o ámbito no autorizado.
- `503`: ámbito de acceso indisponible, MongoDB ausente/no disponible o presupuesto de consulta agotado.

Un conjunto vacío autorizado es `200`; un fallo de MongoDB nunca se representa como lista vacía.

## Persistencia y permisos

MongoDB conserva 90 días desde `registradaEnUtc`, con `_id = EventId`, expiración BSON UTC y TTL sobre `expiresAt`. Las lecturas filtran también `expiresAt > ahora`; no dependen de la ejecución inmediata del barrido TTL. Un replay conserva el evento y la expiración originales. Una identidad repetida con contenido semánticamente igual es idempotente; contenido o metadata divergentes producen error.

La API usa `nexoruta_api` con permiso `find` solo sobre `operational_history_v1`. El worker usa `nexoruta_worker`: lectura/escritura/índices sobre el historial y los permisos existentes limitados a `PasswordRecoveryRequested`. No reutilizar la cuenta raíz desde los procesos de aplicación. El worker crea los índices; la consulta HTTP no requiere permisos DDL. El cliente Mongo es singleton; selección de servidor y conexión tienen límites de 3 s y socket de 5 s; cada query/agregación tiene `MaxTime` de 3 s.

GCP provisiona ambos roles/usuarios mediante el servicio temporal `mongo-provisioner`, que corre durante `prepare` del nodo A, autentica con la credencial raíz protegida ya existente y aplica definiciones idempotentes. No elimina colecciones, datos ni volúmenes. Las credenciales de aplicación se derivan por HMAC-SHA256 con separación de dominio para API y worker. El topic `nexoruta.operacion.v1` requiere 3 particiones y retención Kafka `-1/-1`; si una instalación existente tiene otra configuración, el despliegue se detiene para evitar una modificación silenciosa.

La reconciliación real de usuarios en GCP, índices, TTL físico, perfiles `explain` y medición de rendimiento requieren una aceptación operativa sobre un destino identificado. No reinicializar offsets, borrar recibos ni conectar una base vacía a fronteras activas como procedimiento de recuperación. La evidencia de implementación y las comprobaciones pendientes están en [`aceptación OPT-05`](../../../docs/auditorias/opt-05/aceptacion.md) y en el [plan de implementación](../../../docs/planes/opt-05-historial-y-analitica-nosql.md).
