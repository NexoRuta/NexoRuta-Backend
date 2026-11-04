# CU-08 — Bultos: primer monitoreo

- **Estado:** primer monitoreo del 08/10/2026 cubierto parcialmente; CU-08 es obligatorio y separado. El requisito completo es registrar uno o más bultos por envío; el flujo actual sólo registra uno y no demuestra cumplimiento completo.
- **Módulo:** M02 — Envíos y depósito.
- **Fuente:** `Monitoreo_2026-10-08_CU.xlsx`, CU-08.

## Funcionamiento y reglas

El formulario de [CU-07](CU-07-alta-envios.md) exige un bulto con código, peso en gramos y largo, ancho y alto en centímetros. El código es obligatorio, con máximo de 80 caracteres; las cuatro medidas deben ser positivas. La API valida el rango `0.01`–`999999999`, y el dominio vuelve a comprobar obligatoriedad y valores positivos.

El bulto recibe un UUIDv7 y conserva `OperadorId` y `EnvioId` del alta. PostgreSQL exige la pertenencia del bulto al mismo operador que el envío mediante una clave foránea compuesta. Se persiste en la misma transacción que envío, destinatario y dirección.

## Contrato y verificación

El bulto se envía dentro de `POST /api/envios`; el alta devuelve sus datos y `GET /api/envios` incluye `bultos` para cada registro. Backoffice muestra código, peso y dimensiones con sus unidades.

La prueba de integración verifica desde otra conexión el código y las cuatro medidas persistidas. Las pruebas del cliente y de OpenAPI comprueban los tipos, nombres y límites del contrato; el recorrido del formulario confirma el bulto en Backoffice.

Este monitoreo registra un bulto por alta. El flujo actual no cubre la cardinalidad de uno o más bultos exigida por CU-08 completo (PDF `Laboratorio__NET_2026.pdf`, §5.2 n.° 8; XLSM `CU obligatorios!A11:I11`). CU-08 debe cerrarse como caso obligatorio separado; su falta de múltiples bultos no es una mejora opcional de CU-07. Recepción por escaneo y discrepancias se mantienen fuera de este recorte.
