# Sesiones móviles renovables — M01 / T03.a

La sesión móvil usa la autenticación JWT y el hasher Identity de las cuentas centrales. La renovación tiene una vigencia absoluta configurable de **30 días desde el ingreso** por defecto.

## Contrato HTTP

Los tres endpoints son JSON y envían `Cache-Control: no-store`. El token de renovación es una credencial; no debe viajar en URLs ni logs. Producción requiere HTTPS.

| Endpoint | Entrada | Resultado |
| --- | --- | --- |
| `POST /api/auth/mobile/login` | `email`, `password` | 200 con sesión; 400 por DTO inválido; 401 por credenciales o contexto no admitido; 429 por límite; 503 por configuración de vigencia inválida. |
| `POST /api/auth/mobile/refresh` | `refreshToken` | 200 con nuevo JWT y nuevo refresh; 400 por formato inválido; 401 por token desconocido, consumido, vencido, revocado o acceso inválido; 429 por límite. No requiere un JWT vigente. |
| `POST /api/auth/mobile/logout` | `refreshToken` | 204 idempotente, también si el token es desconocido, ya revocado o pertenece a una sesión vencida; 400 por formato inválido; 429 por límite. |

La respuesta de ingreso/renovación contiene `accessToken`, `tokenType` (`Bearer`), `expiresAtUtc`, `refreshToken`, `refreshExpiresAtUtc` y `sesionId`. Se publica mediante OpenAPI; ningún cliente debe inventar estos campos o decodificar el token opaco para obtener identidad.

La sesión renovable admite cuentas activas de operador con rol. Las demás clases de acceso reciben `401`. El ingreso autentica la cuenta, y el acceso a reparto requiere además las comprobaciones vigentes de perfil, permisos y pertenencia de la operación.

- `GET /api/mobile/perfil` devuelve el perfil de repartidor vigente a la cuenta autorizada. [Contrato de perfil](../../src/NexoRuta.Api/Features/Reparto/Controllers/RepartidoresController.cs) · [Pruebas de perfil y permisos](../../tests/NexoRuta.IntegrationTests/Reparto/RepartidoresHttpTests.cs)
- `POST /api/mobile/jornadas/{jornadaId}/operaciones` y `PUT /api/mobile/jornadas/{jornadaId}/evidencias/{archivoId}` exigen `reparto.ejecutar` y verifican el acceso a la jornada en el servidor. [Contrato de operaciones](../../src/NexoRuta.Api/Features/Reparto/Controllers/OperacionesRepartoController.cs)
- La API permite registrar y retirar instalaciones y consultar o confirmar avisos. El Worker en modo `Push` despacha los avisos mediante FCM cuando está configurado. [Contrato de notificaciones](../../src/NexoRuta.Api/Features/Notificaciones/Controllers/NotificacionesRepartoController.cs) · [Pruebas de Push](../../tests/NexoRuta.IntegrationTests/Notificaciones/PushOutboxTests.cs)

El login web `POST /api/auth/login` sigue siendo el ingreso de los clientes web actuales, con su propia respuesta. Ambos flujos usan el mismo caso de uso de credenciales, hash de contraseña y cuentas; no hay otro registro de usuarios ni otro proveedor de identidad. La app móvil no ofrece registro público.

## Ciclo de vida y seguridad

- JWT de acceso de hasta 15 minutos, limitado por el vencimiento absoluto de la sesión móvil. Emisor, audiencia y clave son los del sistema central. El claim `nexoruta:sesion_movil` vincula el JWT a una sesión revocable.
- Cada petición con ese claim comprueba sesión, usuario, acceso, versión, revocación y vencimiento. La validación de pertenencia/estado/rol y los permisos efectivos del backend siguen siendo obligatorios.
- Refresh opaco aleatorio de 32 bytes (256 bits), codificado base64url en 43 caracteres. PostgreSQL conserva solo su SHA-256 hexadecimal; ni contraseña ni token original se persisten en estas tablas.
- Renovar consume el refresh e inserta su sucesor en una transacción. La sesión conserva su ID y vencimiento. Se mantiene el historial de hashes consumidos para detectar reutilización.
- El bloqueo PostgreSQL de la fila de sesión serializa rotaciones y revocación. Si se reutiliza un refresh consumido, se revoca toda esa sesión, incluido el sucesor. Dos solicitudes concurrentes con el mismo refresh no crean dos sucesores válidos.
- Logout revoca únicamente la sesión identificada por el token, no las otras sesiones del usuario. Una baja, revocación de versión, retirada de rol o suspensión del operador también impide renovar. Se conserva el estado de la sesión para trazabilidad.
- Los instantes del contrato se normalizan a UTC con precisión de microsegundos para conservar exactamente el vencimiento tras persistirlo y recuperarlo.
- `SesionesMoviles:VigenciaRenovacionDias` vale **30** inicialmente y admite un entero de 1 a 365. Cambiar la configuración afecta ingresos nuevos; no extiende sesiones ya emitidas. Límite inicial de refresh/logout: 30 peticiones por minuto/IP; ingreso conserva el límite de 5.

El cliente debe serializar la renovación y guardar el par de tokens de forma segura. Ante una respuesta de renovación perdida, reenviar el token anterior puede activar la detección de reutilización: deberá solicitar ingreso nuevamente a la misma cuenta conservando pendientes. No se implementa una ventana que acepte tokens consumidos.

El contrato de sesión opera sobre los datos que el teléfono ya envió al servidor. `POST /api/mobile/sync-status` recibe una instantánea de la cola móvil por instalación y secuencia para observabilidad. El manejo de pendientes locales, cambio de cuenta y biometría corresponde al cliente móvil. [Contrato de estado de sincronización](../../src/NexoRuta.Api/Features/Reparto/Controllers/EstadoSincronizacionMovilController.cs)

## Persistencia y migración

`20261017182259_AddMobileRefreshSessions` agrega `SesionesMoviles`, `TokensRenovacionMoviles` y una clave alternativa compuesta sobre IDs de acceso/usuario. Una FK compuesta impide mezclar el usuario de una organización con el acceso de otra. Los hashes tienen longitud/formato comprobados; se indexan sesión y vencimiento. La migración no cambia IDs, hashes de contraseña o roles existentes.

Rollback: elimina únicamente las dos tablas nuevas y su restricción adicional. Las sesiones renovables emitidas se pierden y exigirán nuevo ingreso. Usuarios, cuentas y roles permanecen. Se debe coordinar código y esquema al actualizar; no hay soporte para servir contratos anteriores ni un modo de compatibilidad.

La retención y purga de sesiones e historial expirados siguen pendientes de definición. La renovación y revocación de credenciales escriben el estado de sesión en PostgreSQL. Las operaciones autenticadas de negocio aplican el contexto RLS que les corresponde. [Middleware de contexto RLS](../../src/NexoRuta.Api/Core/Security/RlsTransactionMiddleware.cs)

## Evidencia histórica del corte de sesiones

- API Release compilada; 17 pruebas unitarias seleccionadas y 3 de arquitectura correctas.
- 93 pruebas HTTP/integración correctas en PostgreSQL 18 temporal: sesión móvil, JWT, usuario actual, plataforma y alta propia. Se verificaron además los 18 casos de sesión móvil tras retirar la compatibilidad descartada.
- Cobertura nueva: expiración absoluta, rotación con JWT expirado, reutilización, concurrencia, revocación por sesión, cambios de acceso, tokens inválidos, aislamiento por FK, contrato OpenAPI y rollback/reaplicación conservando identidades y hashes.
- 39 pruebas de contrato del cliente web contra el OpenAPI real correctas. El modelo EF no tiene cambios sin migración.
- Base local actualizada desde `ShipmentCommerce` hasta la nueva migración. Se conservaron los IDs y conteos previos: 2 usuarios, 2 accesos, 2 envíos y 2 bultos.

La evidencia de este corte cubre el Backend. [Suite HTTP de sesiones móviles](../../tests/NexoRuta.IntegrationTests/Seguridad/SesionesMovilesHttpTests.cs)

## Entorno local del corte histórico

El puerto 5432 del host estaba ocupado. `compose.local.yaml`, en la raíz del checkout combinado, publica PostgreSQL en `127.0.0.1:55433`, coincidiendo con `appsettings.Development.json`. API local usa `http://localhost:5041`.

Desde la raíz:

```bash
docker compose -f compose.yaml -f compose.local.yaml up -d postgres
```

Desde `backend`, cargar el archivo local ignorado que contiene `Jwt__SigningKeyBase64` y ejecutar:

```bash
set -a
. ./.env.local
set +a
dotnet run --project src/NexoRuta.Api/NexoRuta.Api.csproj \
  --configuration Release --no-build --launch-profile http
```

`.env.local` tiene permisos 600, se excluye de Git y del contexto de imagen Docker; no se versiona su contenido. El override local también lo proporciona como `env_file` a la API de Compose. La clave no se imprimió ni se incluyó en argumentos. No activar bootstrap o migraciones automáticas como parte de ese arranque.

Las pruebas de Testcontainers necesitaron `DOCKER_CONFIG=/tmp/nexoruta-docker-anon` y `DOCKER_AUTH_CONFIG='{"auths":{}}'` por credenciales de Docker Hub rechazadas; la configuración del usuario no se modificó.

Referencias: [CU-01](../cu/CU-01-comercios-usuarios.md), [bitácora](../bitacora-ia/zarsu-franco.md), [protección de refresh tokens, RFC 9700](https://www.rfc-editor.org/rfc/rfc9700.html#name-refresh-token-protection). El uso de rotación/revocación sigue esas recomendaciones; esta API propia no se presenta como implementación de OAuth/OIDC.
