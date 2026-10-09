# CU-07 — Alta individual de envíos: primer monitoreo

- **Estado:** implementado para el recorte del 08/10/2026.
- **Módulo:** M02 — Envíos y depósito.
- **Fuente:** `Monitoreo_2026-10-08_CU.xlsx`, CU-07 y T-02.

## Actor, entrada y resultado

El usuario del comercio elige el operador para cada envío e ingresa nombre del destinatario, dirección y un bulto. El servidor obtiene el usuario y comercio desde la cuenta de [CU-01](CU-01-comercios-usuarios.md), y comprueba que el operador seleccionado exista mediante `IOperadoresRepository`. Cualquier operador registrado puede recibir envíos del comercio, sin relación previa. El formulario no acepta un comercio o usuario creador enviados por el cliente.

`CrearEnvioUseCase` construye destinatario, dirección, envío en estado `Admitido` y bulto. `EfEnviosRepository` guarda los cuatro registros en un único `SaveChangesAsync`, con la transacción de EF Core. Devuelve el identificador del envío y sus propietarios. Backoffice consulta la API y muestra el mismo registro y los datos del bulto tras recargar.

## Contrato y reglas

- `GET /api/operadores`: todos los operadores registrados, disponibles para la cuenta del comercio.
- `POST /api/envios`: `operadorId`, destinatario/dirección y código, peso y dimensiones del bulto; 201 con `EnvioCreado`. Un operador inexistente devuelve 400 sin persistir. El envío y las respuestas de alta/listado conservan `operadorId` y `comercioId` directamente.
- `GET /api/envios`: 200 con todos los envíos del comercio de la cuenta, aunque usen distintos operadores, o los del operador para su usuario interno. La cuenta se identifica con `X-NexoRuta-Acceso` y se comprueba en PostgreSQL.
- Nombre, dirección y código son obligatorios y tienen máximos de 160, 240 y 80 caracteres. Peso y dimensiones admiten `0.01`–`999999999`; el dominio exige valores positivos. Entrada inválida: 400. Selección ausente o inválida: 401. Alta con acceso interno del operador: 403, sin guardar el envío.

## Verificación y límites

Pruebas unitarias: asignación de propietarios desde el usuario actual, rechazo de peso inválido y ausencia de acceso antes de guardar, y filtros de consulta. La integración guarda y lee el envío y bulto desde otra conexión PostgreSQL y comprueba que otro acceso no recibe ese registro. La comprobación funcional usa el formulario Blazor y encuentra el ID confirmado en Backoffice.

Este corte comprende el alta individual. Importación masiva, idempotencia masiva, tarifas y máquina de estados completa quedan para los siguientes casos/hitos. Redis, RabbitMQ y Worker todavía no participan del alta. La CI de backend/frontend compila y ejecuta sus pruebas; una comprobación local no acredita una ejecución remota vinculada a estos cambios.
