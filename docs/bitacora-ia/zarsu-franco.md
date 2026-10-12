# Bitácora individual de IA — Zarsu - Franco

Este archivo reúne mis registros por tarea. Cada entrada indica qué asistencia usé, qué decidí, qué acepté, modifiqué o descarté y cómo verifiqué el resultado.

## 2026-10-06 — Entidades base para el monitoreo del 08/10

- **Titular:** Zarsu - Franco.
- **Fecha:** 06/10/2026 (Uruguay).
- **Componente:** dominio de M01 Administración y configuración y M02 Envíos y depósito.
- **Herramienta:** Codex (asistente de IA).
- **Estado:** implementación generada y compilada; revisión funcional del equipo pendiente.

### Objetivo y fuentes

Solicité preparar únicamente las entidades necesarias para que el equipo integre el alta de un envío desde el portal del comercio y su consulta en el backoffice en el monitoreo del 08/10. Pedí expresamente que no se implementaran casos de uso ni métodos ajenos a las entidades, y que se me consultara cualquier decisión no resuelta por la documentación antes de codificar.

Se consultaron `Laboratorio__NET_2026.pdf` (§§ 5, 8.3 y 9.5), `Entrega_Analisis_Diseno_2026-10-04.pdf` (pp. 5, 8, 9 y 11), `Monitoreo_2026-10-08_CU.xlsx` (hoja `CU 08-10`), `modelo_dominio_preliminar.dbml` y la estructura del repositorio. El modelo DBML se presenta como conceptual, no como esquema físico definitivo. El ADR-001 de la entrega exige organización por módulo y un dominio sin dependencias de infraestructura ni presentación.

Para evaluar los identificadores se consultaron las referencias oficiales de [UUID en PostgreSQL](https://www.postgresql.org/docs/18/functions-uuid.html), [mapeo de tipos de Npgsql](https://www.npgsql.org/doc/types/basic.html) y [`Guid.CreateVersion7()` en .NET](https://learn.microsoft.com/en-us/dotnet/api/system.guid.createversion7?view=net-10.0).

Durante el análisis constatamos que el repositorio solo contenía el esqueleto de Domain, Application, Infrastructure, API, Worker y proyectos de pruebas. Codex me recomendó definir el mapa conceptual completo, pero implementar en código las entidades que exige cada flujo demostrable. Delimité el primer flujo al monitoreo del 08/10 y tomé las decisiones de modelado que figuran debajo.

### Decisiones registradas

| Origen | Decisión | Motivo o alcance |
| --- | --- | --- |
| Yo | Modelar solo las entidades base del hito del 08/10. | Permitir que el equipo implemente los casos de uso sobre una base acotada. |
| Yo | Crear `Destinatario` y `Direccion` como entidades separadas, según el DBML. | Descarté incluir sus datos directamente en `Envio`. |
| Yo, tras evaluar la propuesta de Codex | Usar `Guid` con UUIDv7 y generarlos al construir cada entidad. | PostgreSQL usa el tipo `uuid`; disponer del identificador antes de persistir también favorece flujos futuros de sincronización. |
| Yo | Usar constructores con datos obligatorios y propiedades de escritura privada. | Evitar entidades recién creadas con datos propios vacíos o inválidos. |
| Yo | Registrar el peso en gramos; la interfaz mostrará, por ejemplo, 1000 g como 1 kg. | La conversión de presentación no pertenece a la entidad. Acordé centímetros para las dimensiones. |
| Codex | Ubicar M01 en `Administracion` y M02 en `Envios`, dentro de `NexoRuta.Domain`. | Sigue el ADR-001 y las referencias entre proyectos existentes. |
| Codex | Usar referencias escalares por ID para los vínculos necesarios del primer flujo, sin propiedades de navegación ni mapeos de EF Core. | Mantiene las entidades independientes de infraestructura y deja el mapeo para la integración. |
| Codex | Iniciar `Envio` en `Admitido`; definir por ahora solo ese valor en `EstadoEnvio`. | Es el estado inicial documentado. Las transiciones completas corresponden al siguiente hito. |
| Codex | Validar en los constructores cadenas requeridas, IDs no vacíos y medidas positivas. | Son condiciones propias de cada entidad; no se implementó lógica de un caso de uso. |

### Resultado de la asistencia de IA

Codex creó **nueve entidades** y un tipo de estado, sin modificar API, Application, Infrastructure ni Worker:

- `src/NexoRuta.Domain/Administracion/`: `Operador`, `Comercio`, `OperadorComercio`, `Usuario` y `AccesoUsuario`.
- `src/NexoRuta.Domain/Envios/`: `Destinatario`, `Direccion`, `Envio`, `Bulto` y `EstadoEnvio` (enum con `Admitido`).

Las entidades generan su ID con `Guid.CreateVersion7()`. `Envio` conserva las referencias mínimas a operador, vínculo operador–comercio, destinatario y dirección. `Bulto` conserva código, peso en gramos y largo, ancho y alto en centímetros.

#### Aceptado, modificado y descartado

- **Acepté sin cambios de criterio:** el recorte al hito del 08/10; `Destinatario` y `Direccion` separados; UUIDv7; constructores con setters privados. Acepté estas decisiones, pero todavía no he revisado línea por línea el código generado.
- **Modifiqué:** la sugerencia inicial de usar kilogramos como unidad base; elegí gramos. La representación en kilogramos queda para el frontend.
- **Descarté para este hito:** crear las 26 clases del DBML por adelantado, usar IDs `int`, guardar destinatario y dirección dentro de `Envio`, y adelantar casos de uso, persistencia, autenticación completa o transiciones de estado posteriores.

### Comprobación y límites

Se ejecutó `docker build -t nexoruta-domain-check .` con la imagen de SDK .NET 10 definida por el proyecto. La restauración y publicación de API, Application, Infrastructure y Domain terminaron correctamente; `NexoRuta.Domain` compiló sin errores. Se revisaron los archivos nuevos para detectar espacios finales. No se ejecutaron pruebas funcionales: la integración del flujo aún no existe en este cambio.

Para el CU de alta queda por comprobar, en su integración, que el envío tenga **al menos un bulto** y que operador, comercio, destinatario, dirección y bultos correspondan al mismo contexto de tenant. Las entidades validan sus datos propios y referencias no vacías; no consultan otros registros. `AccesoUsuario` cubre el usuario de comercio del primer hito; los perfiles y el acceso de personal del operador se modelarán con los CU de autenticación y autorización posteriores.

**Mi revisión pendiente con el equipo:** confirmar que los campos mínimos sirven al formulario del portal y a la vista del backoffice, y registrar cualquier ajuste posterior en esta bitácora con su motivo.

## 2026-10-06 — Documentación compartida y bitácora acumulativa

- **Componente/tarea:** README y guías de arquitectura, flujo de CU y uso de IA.
- **Herramienta y finalidad:** usé Codex para reorganizar y redactar documentación de trabajo a partir de las decisiones del laboratorio y de esta conversación.
- **Estado:** documentación redactada; revisión del contenido por el equipo pendiente.

### Decisiones y resultado

| Origen | Decisión | Motivo |
| --- | --- | --- |
| Yo | Mantener **una sola bitácora por integrante**, con una entrada fechada para cada tarea. | Evitar muchos archivos y conservar el historial personal en un lugar. |
| Yo | Exigir la entrada de cada participante antes de crear una PR. | Registrar decisiones y uso de IA de forma continua, como pide la letra. |
| Yo | Publicar las decisiones y buenas prácticas del backend en el repositorio. | La documentación original no está disponible para todos los integrantes. |
| Yo | Exigir un `.md` de especificación por cada CU implementado. | Hacer revisables sus reglas, contratos y criterios de aceptación. |
| Codex | Dejar un resumen en `README.md` y el detalle de arquitectura y alcance en `docs/workflow/`. | Mantener el README legible y dar acceso al contenido original resumido. |
| Codex | Crear una plantilla en `docs/cu/` sin crear todavía archivos de CU no implementados. | Fijar un formato común sin presentar trabajo futuro como terminado. |

- **Aceptado sin modificaciones:** todavía no he aprobado sin cambios la redacción generada para esta tarea. El requisito de registrar finalidad, aceptación, modificaciones y descartes proviene del §9.5 del laboratorio; la redacción nueva requiere mi revisión y la del equipo.
- **Modificado:** la entrada anterior dejó de ser un archivo por tarea y pasó a formar parte de esta bitácora individual; el README pasó de una regla breve a un índice de arquitectura y proceso.
- **Descartado:** crear una bitácora nueva por cada tarea y generar especificaciones vacías para los 30 CU antes de implementarlos.

### Archivos y verificación

Se actualizó `README.md`; se trasladó la entrada de entidades a `docs/bitacora-ia/zarsu-franco.md`; y se crearon `docs/workflow/arquitectura-backend.md`, `docs/workflow/alcance-funcional.md`, `docs/workflow/implementar-cu.md` y `docs/cu/_plantilla.md`. Se verificaron rutas relativas, referencias y espacios finales. Esta tarea solo cambia documentación; no se ejecutaron pruebas funcionales.

**Mi revisión pendiente con el equipo:** confirmar que el resumen distingue correctamente requisitos, decisiones adoptadas, propuestas y funcionalidades todavía no implementadas; actualizarlo cuando se cierren los ADR pendientes.


## 2026-10-08 — Eliminación de OperadorComercio

- **Solicitud:** permitir que un comercio cree envíos con cualquier operador registrado, eliminando el vínculo previo. Mantener las funcionalidades actuales; no implementar el listado de comercios atendidos por operador.
- **Asistencia:** Codex revisó la letra en `Laboratorio__NET_2026.pdf` (§§3.3–3.4), las especificaciones CU-01/CU-07 y los consumidores existentes antes de modificar dominio, aplicación, persistencia, API y cliente web.
- **Resultado:** `Envio` conserva `OperadorId` y `ComercioId` directamente. El selector lista todos los operadores registrados y el alta comprueba su existencia. Se mantienen las cuentas separadas, el bulto y los filtros de lectura. La migración obtiene el comercio antes de eliminar el vínculo y conserva los registros operativos; al revertir reconstruye únicamente los pares con envíos, con nuevos IDs.
- **Verificación:** 8 pruebas unitarias, 13 de integración PostgreSQL y 26 del frontend contra OpenAPI actual y su copia; migración/reversión/reaplicación y modelo EF sin cambios pendientes. Chrome comprobó el alta con un operador nuevo sin vínculo previo y el mismo envío/bulto/comercio en Backoffice con el filtro del operador. Ambas soluciones compilan; las tres advertencias MSB3277 de EF Core en Worker también se reprodujeron en el commit anterior.
- **Límites:** validación en bases temporales, sin modificar la base persistente ni ejecutar CI remota. Backend y frontend deben actualizarse juntos porque las respuestas ahora incluyen `comercioId` en lugar de `operadorComercioId`. Revisión del equipo pendiente.


## 2026-10-08 — Contexto de usuario por petición y cancelación de Blazor

- **Solicitud:** eliminar la consulta repetida del usuario actual y conectar el token de cancelación del portal Blazor.
- **Asistencia y cambio:** Codex conservó el contexto validado por autenticación en `HttpContext.Items`; `UsuarioActualHttp` verifica el principal y reutiliza ese contexto durante la misma petición. Commerce pasa un token propio a las consultas y al alta, y lo cancela/libera en `Dispose`, tratando la cancelación esperada sin un mensaje de error.
- **Evidencia:** la prueba previa reprodujo tres consultas al autenticar y leer dos veces el usuario. Después se comprueba una sola consulta, validación independiente por petición, separación de usuarios, rechazo de accesos inválidos y de contexto incompatible con el principal, y respeto de la cancelación. Las pruebas del cliente comprueban la cancelación real de sus tres llamadas HTTP utilizadas por el componente.
- **Verificación funcional:** Chrome navegó fuera del formulario durante una consulta y un alta pendientes; un proxy temporal registró la cancelación de ambas conexiones y la navegación continuó sin error del circuito. El proxy retuvo el POST antes de enviarlo a la API para no crear un envío de prueba. API y Commerce locales se reconstruyeron con los cambios, manteniendo PostgreSQL en 5432.
- **Límites:** la cancelación no revierte operaciones ya confirmadas por la API. El contexto se reutiliza solo durante la petición; no se agrega caché entre peticiones ni se cambia el esquema de autenticación. La CI remota no se ejecutó. Revisión del equipo pendiente.


## 2026-10-08 — Repositorios separados para cuentas y empresas

- **Solicitud:** separar el catálogo de operadores de los accesos de usuarios y agregar `IComerciosRepository` siguiendo el mismo criterio.
- **Asistencia y cambio:** Codex extrajo las consultas de operadores a `IOperadoresRepository`/`EfOperadoresRepository`; el selector y `CrearEnvioUseCase` usan esa dependencia. Agregó `IComerciosRepository`/`EfComerciosRepository`, con listado ordenado y consulta por ID. Ambos pares se registran por scope y propagan `CancellationToken`. Accesos conserva la consulta de cuentas y pertenencias, incluyendo nombres de la organización en una sola consulta.
- **Verificación:** 57 pruebas pasaron antes del cambio. Después pasaron 59: 8 unitarias, 22 de integración PostgreSQL y 29 del frontend contra la API actual. Las pruebas nuevas resuelven ambos repositorios por DI, consultan empresas sin usuarios ni envíos, comprueban orden, consulta por ID, resultado nulo cuando no existe y cancelación. La solución backend compila con las tres advertencias preexistentes de EF Core en Worker.
- **Comprobación local:** API reconstruida; cuentas, operador disponible, filtros y respuestas 200/401/403/400 correctos. Se mantiene una sola consulta de acceso por petición y OpenAPI coincide con el contrato del frontend. Los nueve servicios siguen activos y PostgreSQL conserva 5432.
- **Alcance:** repositorios de lectura e integración con las funcionalidades existentes. El repositorio de comercios queda registrado y comprobado para consultas internas; no se agregaron pantallas, endpoints ni la consulta de comercios atendidos por operador. Sin migraciones, cambios de datos ni ejecución de CI remota. Revisión del equipo pendiente.


## 2026-10-08 — Revisión de coherencia de rutas, entidades y configuración

- **Solicitud:** publicar el catálogo global como `/api/operadores` y revisar incongruencias similares en todo el código.
- **Revisión:** Codex recorrió dominio, aplicación, infraestructura, controladores, autenticación, DTOs, cliente HTTP, páginas de ingreso, formularios, pruebas, configuración local, plantillas de Worker/Tracking/Mobile y referencias de scripts/CI. Las empresas siguen separadas de sus usuarios; los accesos representan pertenencias y los envíos guardan operador y comercio directamente. Se mantuvieron las migraciones y pruebas históricas necesarias para conservar y migrar datos.
- **Correcciones:** ruta y consumidores/OpenAPI/pruebas sincronizados; mensaje de pertenencia con operador o comercio; excepción `OperadorNoEncontradoException` en archivo propio; nombres de pruebas y textos de Backoffice referidos a cuentas; ejemplo `.http` con rutas existentes. Development de las webs apunta al perfil API 5041 y permite elegir Compose 5000 mediante `Api__BaseAddress`; las URLs de Compose y PostgreSQL 5432 se conservan.
- **Documentación:** estado actual de arquitectura y conteos de pruebas separados de la evidencia histórica del 7 de octubre; módulos alineados con la hoja `Módulos` del Excel de responsables; distinción entre selección de cuentas implementada y autenticación por credenciales pendiente. Los usuarios del comercio pueden crear envíos, sin exigir que sean propietarios.
- **Verificación:** 60 pruebas pasaron: 8 unitarias, 22 de integración y 30 del frontend contra copia y API real. Ambas soluciones compilan; backend conserva tres advertencias EF Core preexistentes en Worker. Una API temporal con base propia comprobó el mismo catálogo para dos comercios sin envíos, ruta anterior 404, estados 200/201/400/401/403, creación con un operador nuevo y aislamiento de lectura por empresa. Un empleado no propietario también creó un envío; IDs de usuario/comercio enviados por el cliente fueron ignorados en favor de la cuenta validada.
- **Prueba de interfaz:** Chrome confirmó login, catálogo y envío `01a11e15-108d-71e0-824f-a9f0b907b789` con su bulto en Backoffice, usando Development y API 5041 sin override de `Api__BaseAddress`. Compose actualizado sirve la ruta nueva y coincide con el contrato versionado; nueve servicios activos.
- **Límites:** no se implementaron nuevas funcionalidades, perfiles, autenticación real ni RLS. No se ejecutó MAUI en dispositivos, CI ni despliegue remoto. Los envíos de verificación se guardaron únicamente en la base temporal de esta revisión. Revisión del equipo pendiente.
