# CU-05 — Identidad visual del operador

## Estado y trazabilidad

Especificación funcional de CU-05. La tabla de decisiones del usuario autoriza las decisiones de producto indicadas abajo; **no se atribuyen al PDF**. La inspección directa de `docs/alcance/Laboratorio__NET_2026.pdf` confirma el requisito obligatorio 5 de §5.1 (marca, colores y contacto en Commerce, seguimiento público y comunicaciones), la independencia de operadores de §§3.2–3.3, las aplicaciones de §4, la autorización/aislamiento de §§6.3 y 6.5 y la demostración con dos operadores de §6.5/§8.3. Ninguna fuente primaria enumeraba los campos ni sus límites.

La hoja `CU obligatorios` del XLSM identifica CU-05 como M01 obligatorio y asigna “Operador / administrador”. La fila de M01 en `Módulos` menciona configuración diferenciada, editable sin despliegue e historial; la regla de historial consultable de esta especificación proviene de la decisión de producto del usuario, no de un criterio específico del PDF.

## 1. Alcance autorizado

Un usuario autorizado de un operador administra una identidad visual separada del nombre administrativo. La configuración vigente se refleja únicamente en consumidores que tengan un operador concreto: Backoffice (edición y vista previa), Commerce (operador seleccionado o asociado al envío), seguimiento público (operador del envío) y comunicaciones existentes dirigidas al destinatario. Las pantallas sin operador concreto mantienen la identidad NexoRuta. Los consumidores sin personalización usan el nombre actual del operador y los tokens semánticos predeterminados.

No se exponen nombre, correo, teléfono ni otros datos del propietario o contactos administrativos por inferencia. Solo se publican los campos configurados expresamente en esta identidad visual.

## 2. Campos y validación

| Campo | Reglas |
|---|---|
| Nombre comercial | Opcional; máximo 160 caracteres. Se recorta el espacio externo; vacío se guarda como `null` y muestra el nombre administrativo actual del operador. No modifica `Operador.Nombre`. |
| Logo | Opcional; reemplazable y eliminable explícitamente. PNG o JPEG estático; máximo 1 MiB, ancho y alto de hasta 2048 px y máximo 4.194.304 píxeles. El servidor comprueba firma/formato y decodifica el contenido; MIME y extensión enviados por el cliente no son autoridad. Se rechazan SVG, animaciones, archivos malformados, formatos no admitidos y dimensiones/tamaños excesivos. |
| Correo público | Opcional; máximo 254 caracteres, trim y dirección sintácticamente válida, sin nombre para mostrar. Un valor vacío elimina el dato público. |
| Teléfono público | Opcional; máximo 32 caracteres. Admite dígitos ASCII y separadores `+`, espacio, `(`, `)`, `.` y `-`; como máximo 15 dígitos y al menos 7; `+` solo al inicio. Un valor vacío elimina el dato público. El enlace `tel:` usa la forma validada sin separadores. |
| Color primario y secundario | Opcionales; formato exacto `#RRGGBB`, normalizado a mayúsculas. Se conservan aun cuando no sean aptos para una combinación accesible de texto/control. |

Se rechazan valores desconocidos en requests de configuración. La validación de servidor es obligatoria también cuando el cliente ya limita extensión o formato.

## 3. Autorización, aislamiento e historial

- Administrar y consultar el historial requiere permiso vigente `configuracion.gestionar`, sesión/organización activa y contexto persistido de tipo `Operador` con su `OperadorId` obtenido del servidor. El permiso por sí solo no basta: un `Comercio` con el mismo permiso recibe 403. No se restringe la administración al propietario.
- El endpoint de escritura no acepta un `OperadorId` suministrado como autoridad. Toda escritura y consulta de historial queda atada al operador resuelto en el contexto autenticado y a RLS.
- La tabla versionada es la fuente de configuración: no existe una fila personalizada antes del primer cambio; la versión 0 representa fallbacks. Cada cambio efectivo crea un snapshot completo e inmutable con número de versión, autor, fecha UTC, campos públicos y logo. Una operación sin cambios normalizados no crea versión. El historial se consulta en páginas de 10 y usa un cursor de versión para alcanzar cambios anteriores, sin perder versiones persistidas.
- La transacción que inserta el snapshot es atómica. La clave única `(OperadorId, Version)` y la versión esperada detectan concurrencia; una colisión o versión obsoleta retorna 409 y preserva la versión ganadora. No hay restauración de versiones en CU-05.
- La lectura de historial devuelve solo el historial del operador autorizado. Los consumidores públicos reciben una proyección de la versión vigente, nunca autor, historial, IDs de propietario ni datos administrativos.

## 4. Persistencia y contratos

La inspección de código fuente no encontró un mecanismo existente de almacenamiento de archivos. Se persiste el logo validado como `bytea` dentro del snapshot de versión en PostgreSQL: así cada versión conserva su configuración completa, la escritura de imagen e historial es atómica y no se introduce almacenamiento externo. El endpoint entrega el tipo detectado/validado y la imagen solo a consumidores de identidad visual.

Contratos implementados, sin cambiar el significado de los existentes:

- `GET /api/configuracion/identidad-visual`: configuración vigente del operador y versión esperada.
- `PUT /api/configuracion/identidad-visual`: multipart con campos, versión esperada, logo opcional y señal explícita para quitarlo; 409 en conflicto.
- `GET /api/configuracion/identidad-visual/versiones?limite=10&antesDeVersion=...`: historial autorizado, solo lectura y paginado por cursor; cada respuesta incluye las versiones de la página y el cursor anterior cuando hay más resultados.
- `GET /api/operadores/{operadorId}/identidad-visual`: proyección pública acotada para Commerce y solo para un operador elegible/seleccionado.
- `GET /api/public/seguimiento/{token}` conserva `estado` y añade la proyección pública del operador vinculado al token.

Las proyecciones públicas contienen únicamente nombre comercial resuelto con fallback, correo/teléfono configurados, colores y logo si existe. La proyección pública no expone número de versión, autor ni historial. Nunca incluye datos de propietario ni datos administrativos.

En Development, la API genera OpenAPI en `GET /openapi/v1.json`; no se mantiene un archivo OpenAPI estático. La respuesta vigente incluye estas rutas y los esquemas `IdentidadVisualAdministracionResponse`, `IdentidadVisualHistorialResponse`, `IdentidadVisualPublicaResponse` e `IdentidadVisualVersionResponse`.

## 5. Consumidores de comunicaciones

La inspección de los canales existentes encontró el correo de código de cancelación dirigido al destinatario; los correos de recuperación de cuenta son de identidad y quedan fuera. Las notificaciones push halladas son de operación/reparto, no al destinatario; no se encontró un canal ya implementado de avisos de estado por correo/SMS. CU-05 integra la identidad pública en el recorrido real de correo de cancelación (nombre, contacto, acento accesible y logo si hay); no crea un canal de notificación nuevo.

El correo mantiene una alternativa de texto. La plantilla HTML escapa los textos, usa logo inline validado si existe y solo usa color de marca como fondo si el contraste con texto claro u oscuro alcanza WCAG AA; si no, usa superficie/texto semánticos predeterminados y conserva el color como acento.

## 6. Presentación accesible

La pantalla de edición agrupa marca, colores y contacto, prioriza “Guardar cambios”, incluye una vista previa con datos ficticios identificados y distingue cambios sin guardar, guardado, éxito y error. Una carga inicial asíncrona de estructura conocida muestra skeleton; al guardar se conserva formulario y foco, se impide doble envío y se anuncia el estado. Se conservan los tokens semánticos de acción, error, éxito, advertencia y foco, independientes de los colores del operador.

En vista previa y consumidores, un color personalizado puede ser acento. Texto normal sobre un fondo requiere contraste 4,5:1; texto grande y componentes/señales visuales, 3:1 según el criterio aplicable. Cuando una combinación no llega, la superficie usa un fallback accesible, se informa el motivo y el valor configurado se conserva. El estado no depende solo del color. Logo con proporción conservada y nombre alternativo contextual; contacto con etiqueta y `mailto:`/`tel:` cuando corresponda. Se verifican teclado, foco visible, zoom 200 % y reflujo a 320 CSS px.

Los tokens de marca se limitan al contexto del operador actual; cambios de operador no pueden destellar la identidad anterior. Las acciones y estados semánticos no se tematizan con colores personalizados.

## 7. Criterios de aceptación

1. Dos operadores pueden guardar configuraciones distintas; una cuenta de operador con `configuracion.gestionar` lee/escribe solo la propia, y un comercio con igual permiso no puede escribir ni consultar historial de administración.
2. Fallback sin configuración usa el nombre administrativo vigente, no muestra contactos ni logo no configurados y presenta NexoRuta en pantallas sin operador concreto.
3. Nombre, correo, teléfono, colores y logo respetan límites y rechazan valores inválidos en servidor. Un archivo con extensión/MIME engañosos, contenido malformado, SVG, animación, más de 1 MiB o dimensiones fuera de límite se rechaza sin crear una versión.
4. Cambios efectivos guardan snapshot, autor, fecha y versión de forma atómica; un no-op no incrementa. Un intento obsoleto devuelve 409 sin pérdida de datos. El historial no tiene acción de restauración.
5. Backoffice edita y previsualiza; Commerce muestra la identidad seleccionada/asociada; seguimiento público muestra solo la identidad del envío del token; la comunicación de cancelación al destinatario usa los datos configurados con fallback.
6. La respuesta pública no contiene autor, historial, contactos administrativos ni IDs no necesarios. Commerce y seguimiento no pueden resolver accidentalmente la marca de otro operador.
7. Combinaciones sin contraste suficiente no se usan como texto/control: se informa, se conserva el color elegido y se muestra fallback semántico; los colores de estado/foco siguen independientes.
8. Pruebas enfocadas de backend/HTTP, persistencia PostgreSQL efímera, frontend y comunicación cubren permisos, dos operadores, aislamiento, historial, concurrencia, validación y contrato público. La revisión real en navegador cubre escritorio y móvil, teclado, foco, carga, guardado, reflujo y contraste.

## 8. Decisiones técnicas de implementación

- Decodificación de imágenes: se usa `StbImageSharp` para decodificar el contenido, tras verificar que el repositorio no contiene un mecanismo equivalente. Para PNG también se validan estructura y CRC de todos los chunks; JPEG se decodifica completamente. MIME/extensión no son autoridad.
- Se usa el historial como tabla de snapshots y estado vigente, evitando duplicar una tabla “actual” que pudiera divergir del historial.
- El historial devuelve primero las versiones recientes y permite seguir hacia atrás con `antesDeVersion`; no incluye restauración.
- No instalar servicios externos ni modificar bases persistentes. Las migraciones se prueban solo con PostgreSQL/PostGIS efímero.

## 9. Estado

La especificación se documentó antes de implementar; las decisiones de producto permanecen atribuidas al usuario, no al PDF.

Implementación completada en el backend, persistencia versionada, contratos públicos mínimos, Backoffice, Commerce, seguimiento y correo de cancelación existente. El historial permite consultar todas las páginas con aislamiento por operador. No se añadieron canales de comunicación ni infraestructura de archivos externa.

### Compilación y modelo

- API: `dotnet build src/NexoRuta.Api/NexoRuta.Api.csproj --no-restore --verbosity quiet` — compilación correcta, 0 advertencias y 0 errores.
- Modelo EF: inmediatamente después del build, `dotnet ef migrations has-pending-model-changes --no-build --project src/NexoRuta.Infrastructure/NexoRuta.Infrastructure.csproj --startup-project src/NexoRuta.Api/NexoRuta.Api.csproj --context NexoRutaDbContext` — salida “No changes have been made to the model since the last migration”, código 0.
- Worker: `dotnet build src/NexoRuta.Worker/NexoRuta.Worker.csproj --no-restore --verbosity quiet` — compilación correcta, 0 advertencias y 0 errores.
- Backoffice, tras el ajuste de navegación de abajo: `dotnet build src/NexoRuta.Backoffice/NexoRuta.Backoffice.csproj --no-restore --verbosity quiet` — compilación correcta, 0 advertencias y 0 errores. El primer intento mientras el host seguía en ejecución tuvo reintentos de copia por el ejecutable bloqueado; se detuvo el host y la compilación limpia posterior pasó. Una compilación completa no incremental anterior informó 3 advertencias nullable preexistentes en `Features/ZonasCobertura/{Pages/DetalleModel.cs,Pages/CrearModel.cs}`; no se originan en CU-05.
- Commerce y Tracking ya habían compilado sin advertencias en la verificación enfocada anterior; no se recompilaron porque esos proyectos no cambiaron en este cierre.

### Migraciones, OpenAPI y HTTP

- PostgreSQL/PostGIS efímero `postgis/postgis:18-3.6-alpine`, sin volumen y sin Compose: la actualización desde una base vacía aplicó la cadena hasta `20261022015742_AddOperatorVisualIdentity` y EF informó `Done`.
- En una segunda base del mismo contenedor, EF se llevó primero a la migración inmediatamente anterior, `20261020234154_RemoveDuplicateCourierRules`, y después se actualizó a `20261022015742_AddOperatorVisualIdentity`; ambos comandos informaron `Done`. No se usaron bases persistentes.
- Un `SELECT` auxiliar a `__EFMigrationsHistory` no fue una comprobación válida: omitió las comillas del identificador PostgreSQL con mayúsculas y devolvió “relation does not exist”. La evidencia de aplicación son las salidas exitosas de EF descritas arriba, no ese `SELECT`.
- `GET /openapi/v1.json` en Development respondió 200 (OpenAPI 3.1.1). Se observaron las operaciones de configuración, historial, proyección de operador y seguimiento público, junto con los cuatro esquemas CU-05 listados en §4. El contrato de seguimiento conserva `estado` y añade `identidad`.
- La prueba HTTP directa de seguimiento con token activo respondió 200, estado `Admitido` e identidad pública configurada. La proyección no incluyó historial ni autor.

### Automatización y comunicaciones

- En la ejecución enfocada anterior pasaron 14 pruebas `IdentidadVisualValidatorTests`, 1 `ColorMarcaTests` y 6 pruebas de integración HTTP/PostgreSQL efímero para dos operadores, aislamiento, permiso de empleado/comercio, historial paginado, concurrencia, logo, proyección pública y comunicación. No se ejecutaron suites completas ni se repitieron estos tests porque el código backend no cambió en este cierre.
- La prueba de correo `RecipientCancellationEmailUsesOnlyExplicitPublicBrandAndAccessibleLogo` usa `CorreoPrueba` en memoria y verifica nombre, contacto, `mailto:` y logo inline. No usa SMTP ni produjo envíos reales. El Worker se compiló en esta sesión.

### Navegador y accesibilidad

- Edge headless 155 ya había comprobado Backoffice (edición, vista previa, guardado, versión, logo válido, fallback para `#FFFF00`, etiquetas y foco) y Commerce (operador seleccionado y su identidad/contacto/colores).
- En este cierre se abrió Tracking con un token válido de un envío sembrado en PostgreSQL efímero: la pantalla mostró “Marca de seguimiento”, correo/teléfono públicos y estado `Admitido`. El contrato del API también respondió HTTP 200.
- Se recorrió con teclado el login, el control para desplegar la navegación y todos los enlaces y controles de la pantalla de identidad: nombre, logo, activación de los dos colores, contacto y “Guardar cambios”. También se recorrieron en Tracking los enlaces, `mailto:`, `tel:` y la acción de correo. El foco `:focus-visible` fue visible en navegación, campos y botones; no se guardó ni se solicitó un correo.
- La comprobación de reflujo al 200 % se realizó en Edge mediante el equivalente de viewport de escritorio de 720 CSS px a DPR 2 (1440 píxeles físicos). Tras el ajuste no hubo overflow horizontal; Tracking también pasó con ese viewport. A 320 CSS px el documento de Backoffice quedó en 305 CSS px disponibles por la barra vertical y sin overflow.
- La primera comprobación a 200 % encontró overflow en la barra de navegación (`navbar-expand-sm`, ancho de documento 1107 CSS px frente a 705 disponibles). Se corrigió el breakpoint para colapsarla hasta `lg`, se vinculó el toggler con `navbarSupportedContent` y se localizó su etiqueta accesible; el build y la revisión de navegador posteriores pasaron. El atajo Ctrl+Plus enviado por CDP no cambió el zoom nativo, por lo que el resultado se registra como emulación equivalente de reflujo, no como uso del control de zoom del navegador.

### Pendientes que no se presentan como aprobados

- La revisión nativa previa de Gentle AI/RDD falló antes de iniciar un ciclo de revisión; no existe aprobación ni receipt.
- El espejo de Engram sigue pendiente: el runtime no registró una identidad de sesión autorizada para esta sesión, así que no se intentó una escritura atribuyéndole otra identidad.
- El control de zoom nativo de Edge al 200 % no pudo verificarse; la revisión visual comprobó el reflujo equivalente con viewport de 720 CSS px y DPR 2, no el ajuste nativo del navegador.
- No hubo stage, commit, push, cambio de rama, modificación de base persistente ni recreación de Compose.
