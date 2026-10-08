# CU-01 — Comercios y usuarios: primer monitoreo

- **Estado:** implementado para el recorte del 08/10/2026.
- **Módulo:** M01 — Administración y configuración.
- **Fuente:** `Monitoreo_2026-10-08_CU.xlsx`, CU-01; `Laboratorio__NET_2026.pdf`, §§3.3, 4 y 5.1; selección de cuenta solicitada por el equipo.

## Actores y pertenencia de las cuentas

`Operador` es el tenant; `Comercio` es su cliente; `Usuario` es la persona que accede. La letra dice en §3.3, página 2, que un mismo comercio puede operar con más de un operador logístico. El comercio puede enviar con cualquier operador registrado. No existe una entidad ni tabla `OperadorComercio`: cada envío guarda directamente `OperadorId` y `ComercioId`.

`AccesoUsuario` asigna una cuenta a una organización: tiene `OperadorId` o `ComercioId`, nunca ambos. La base exige una sola pertenencia por usuario. La cuenta del operador entra únicamente a Backoffice y la cuenta del comercio únicamente al portal. No se crea otra cuenta ni otro acceso cuando se agrega un operador que atiende al comercio.

La cuenta inicial del comercio tiene `EsPropietario = true` y representa al dueño de prueba. La cuenta del operador no puede tener esa marca. Los tipos Operador/Comercio identifican la organización de la cuenta; los perfiles laborales y sus permisos siguen fuera de este recorte.

`Destinatario` contiene los datos de entrega declarados en un envío; su `OperadorId` delimita datos operativos, sin crear una cuenta o membresía para esa persona. Tracking sigue siendo una plantilla: no se implementó todavía el seguimiento mediante enlace público.

## Inicialización e ingreso

La API aplica migraciones y `DatosInicialesSeeder` inicializa un operador, un comercio, la cuenta del dueño y una cuenta del operador. El seed conserva las identidades y los envíos existentes y no duplica datos al repetirse. Su configuración está en `AccesoInicial`: `UsuarioEmail`, `OperadorUsuarioEmail`, `OperadorNombre` y `ComercioNombre`.

Las cuentas iniciales son `usuario@nexoruta.local` (dueño del comercio) y `operador@nexoruta.local` (usuario interno del operador). Las listas de `/ingresar` se leen de PostgreSQL; la entrada al portal muestra el usuario y comercio, sin seleccionar un operador. El servidor comprueba el tipo de cuenta antes de emitir la cookie. Cada aplicación usa cookies de sesión y antiforgery independientes y ofrece `POST /salir` para cambiar de cuenta.

En cada petición de la API, `AccesoSeleccionadoHandler` consulta la cuenta una vez y conserva su `ContextoUsuario` en `HttpContext.Items`. `UsuarioActualHttp` reutiliza ese resultado y comprueba que corresponda al ID de acceso del principal, sin volver a consultar PostgreSQL. El resultado se limita a la petición actual; cada petición nueva vuelve a validar la cuenta. Ambos respetan la cancelación de la petición.

## Elección del operador y contratos

El dueño selecciona el operador **al crear cada envío**. La API lista todos los operadores registrados y comprueba que el elegido exista antes de persistir. No exige envíos anteriores ni un vínculo previo con el comercio. El comercio de origen y el usuario creador se obtienen desde la cuenta del solicitante; no se eligen desde el formulario.

- `GET /api/accesos?tipo=Comercio|Operador`: cuentas disponibles para la entrada, sin selección previa.
- `GET /api/usuarios/actual`: cuenta seleccionada y organización de pertenencia. Una cuenta de comercio tiene `OperadorId = null`; una cuenta del operador tiene `ComercioId = null`.
- `GET /api/comercio/operadores`: todos los operadores registrados, disponibles para el comercio de la cuenta. Solo admite cuentas de comercio.
- Las operaciones envían `X-NexoRuta-Acceso` para identificar la cuenta seleccionada. Selección ausente o inválida: 401. Operación reservada al comercio con cuenta del operador: 403.
- Un `operadorId` inválido, ausente o inexistente en el alta devuelve 400 y no guarda datos. Un operador registrado es elegible aunque nunca haya trabajado con el comercio.
- El comercio consulta sus envíos con todos sus operadores; Backoffice consulta los envíos del operador de su cuenta.

La interacción cliente–operador queda registrada en los envíos. El listado de comercios con los que ha trabajado un operador todavía no se implementa; no se agregan pantallas ni endpoints para esa consulta.

## Límites y verificación

La selección es sin credenciales para este monitoreo. `AccesoSeleccionadoHandler` no prueba identidad y debe sustituirse al incorporar autenticación real. No se implementaron CRUD, perfiles laborales, RLS ni otros CU. Los filtros y claves foráneas se verifican sin afirmar aislamiento mediante RLS.

La migración convierte los accesos anteriores en pertenencias a la organización, conserva sus IDs cuando había una sola cuenta y agrupa vínculos del mismo usuario/comercio. Ante organizaciones incompatibles para la misma cuenta, falla sin modificar los datos. La migración posterior `ShipmentCommerce` convierte el ID del vínculo de cada envío en el `ComercioId` correspondiente antes de eliminar `OperadoresComercios`, conservando usuarios, cuentas, envíos, destinatarios, direcciones y bultos. Al revertirla reconstruye los vínculos necesarios para los envíos existentes con nuevos IDs; no recupera vínculos anteriores sin envíos. Las migraciones históricas se mantienen para actualizar bases existentes. El backend y el cliente web deben actualizarse juntos porque las respuestas reemplazan `operadorComercioId` por `comercioId`.

Pruebas: seed repetido y reparación, cuentas separadas, dueño inicial, mismo comercio/cuenta con dos operadores, elección por envío, rechazo de operador inexistente y cuenta de operador, lectura por comercio/operador y conservación de datos anteriores. Cada escenario PostgreSQL usa una base propia que se elimina al terminar. Las pruebas del cliente verifican las respuestas, selección obligatoria y contrato JSON con OpenAPI. La CI remota de estos cambios no se ejecutó.
