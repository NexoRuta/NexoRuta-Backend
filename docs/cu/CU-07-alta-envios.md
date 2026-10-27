# CU-07 — Alta individual de envíos: primer monitoreo

- **Estado de este primer monitoreo:** implementado para alta individual; no representa el cierre del CU-07 completo. El requisito obligatorio también incluye alta por archivo e idempotencia al reimportar lotes completos o parciales; ambas están pendientes.
- **Módulo:** M02 — Envíos y depósito.
- **Fuente:** Monitoreo_2026-10-08_CU.xlsx, CU-07 y T-02.

## Actor, entrada y resultado

El usuario autenticado del comercio crea un envío y elige un operador disponible para ese envío. El servidor obtiene el usuario y ComercioId del JWT validado y del acceso persistido; el cliente no elige el comercio. OperadorId se valida y queda guardado en el envío. No hay relación previa ni permanente comercio–operador y no se crea una tabla de clientes del operador.

CrearEnvioUseCase valida actor, destinatario, dirección y bulto. La escritura pasa por la operación acotada de base de datos que guarda el envío y sus datos hijos en una transacción; el outbox publica los eventos posteriores. El operador consulta ese envío mediante su propio contexto si es el OperadorId elegido. Ninguno obtiene envíos de otro actor. La API niega creación para un operador, aunque envíe campos manipulados.

## Contrato y reglas

- GET /api/operadores: catálogo de operadores disponibles para elegir. Contrato único; no hay alias de compatibilidad.
- POST /api/envios: requiere Bearer de una cuenta comercio y OperadorId, nombre y correo obligatorio del destinatario, dirección y un bulto. El contexto ComercioId proviene de la sesión. La selección del operador se valida y guarda en ese envío, no en una membresía.
- GET /api/envios: consulta los envíos del comercio autenticado o del operador autenticado para sus asignaciones.
- La cancelación autenticada y su aviso al repartidor se describen en [cancelación de envíos](cancelacion-envios.md); la cancelación pública requiere el código enviado al correo obligatorio del destinatario; el servicio SMTP real sigue pendiente.
- El operador puede ser distinto en cada envío. Para los hijos de un envío el aislamiento sigue el vínculo del envío.
- Nombre, dirección y código son obligatorios; los límites de texto y dimensiones siguen el contrato OpenAPI.
- La cabecera X-NexoRuta-Acceso no autentica ni selecciona al actor. Las rutas de envío requieren JWT Bearer.

## Pruebas/evidencia

- backend/tests/NexoRuta.UnitTests/Envios/CrearEnvioUseCaseTests.cs: el comercio usa la cuenta propia y el operador elegido; un acceso operador no crea envíos.
- backend/tests/NexoRuta.IntegrationTests/Envios/EnvioPostgresRoundTripTests.cs: round-trip en PostgreSQL temporal, cuenta comercio, múltiples operadores por envío e integridad del bulto.
- backend/tests/NexoRuta.IntegrationTests/Administracion/PlataformaHttpTests.cs: CreatedCommerceCanSelectDifferentOperatorsWhileEachActorSeesOnlyItsShipments.
- backend/tests/NexoRuta.IntegrationTests/Seguridad/RlsPolicyTests.cs y RlsRestrictedHttpFlowTests.cs: roles restringidos, visibilidad de ambas partes, filas hijas y operaciones directas limitadas por privilegios/RLS.

La importación de archivos y su idempotencia son parte obligatoria del CU-07 completo (PDF `Laboratorio__NET_2026.pdf`, §5.2 n.° 7; XLSM `CU obligatorios!A10:I10`, `D10`), aunque no estaban cubiertas por este primer monitoreo. Siguen sin implementar a la fecha de la verificación del 23/10/2026. Tarifas corresponden a CU-09; la máquina de estados completa se cierra por su caso de uso.

El comercio revisa y confirma las coordenadas antes de planificar el envío. [Contrato de destinos y geocodificación](destinos-envios.md).
