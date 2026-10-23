# CU-02 — Zonas de cobertura y franjas de entrega

**Estado actual — cierre funcional (2026-10-19):** CU-02 está implementado y validado en el alcance acordado. Incluye dominio, PostgreSQL/PostGIS, RLS, API/ApiClient, páginas de Backoffice con Mapbox, catálogo departamental importado y restricción territorial a Uruguay. Las pruebas enfocadas están documentadas abajo; el usuario confirmó manualmente que quedó bien después de la importación y la restricción territorial. Esa confirmación no se presenta como un recorrido automatizado de Mapbox. Los bloqueos y estados previos se conservan como evidencia histórica, no como pendientes vigentes. Ver «Cierre y validación manual» al final.

El relevamiento y el diseño acordado conservan la trazabilidad funcional; las secciones identificadas como históricas describen lo observado en cada corte, mientras que el resumen inicial y el cierre final expresan el estado vigente. Los detalles identificados como decisiones acordadas provienen de los prompts de diseño e implementación; las propuestas técnicas se distinguen de los requisitos textuales de las fuentes.

## Decisiones principales

| Tema | Decisión |
|---|---|
| Propietario y UI | El usuario operador con permiso administra su propio tenant desde el Backoffice existente. Commerce y Plataforma no reciben acceso operativo a este módulo. |
| Mapa y geometría | Mapbox GL JS con su plugin de dibujo; GeoJSON RFC 7946 en la API; PostGIS guarda siempre MultiPolygon con SRID 4326. |
| Inicio de cobertura | Se puede dibujar una geometría propia o copiar un límite departamental oficial ya importado localmente, y luego editarla. No se infieren polígonos a partir de nombres o puntos. |
| Franjas | Oferta semanal por zona y día ISO, en hora local America/Montevideo; sin feriados, cupos ni reservas. |
| Coincidencias | La consulta devuelve todas las zonas activas del operador que cubren el punto. No elige una fila arbitraria. |
| Historia | Cada cambio crea una revisión inmutable con una fotografía de toda la zona, geometría y franjas. La columna de concurrencia por sí sola no sustituye esa historia. |
| Envíos | CU-02 entrega zona, revisión y oferta. CU-07/otros casos resolverán selección y promesa concreta; aquí no se cambia Envio ni su alta. |

## Trazabilidad y diferencias entre fuentes

### PDF del laboratorio

Fuente obligatoria: [Laboratorio__NET_2026.pdf](../../../docs/alcance/Laboratorio__NET_2026.pdf).

| Referencia | Lo que exige o describe la fuente | Consecuencia para CU-02 |
|---|---|---|
| §§3.1–3.3, pp. impresas 2–3 (PDF pp. 3–4) | El servicio atiende a varios operadores, cada uno con sus zonas y reglas; los operadores no acceden a datos ajenos. Un comercio puede trabajar con más de un operador. | La configuración pertenece al operador. No se crea una relación permanente comercio–operador. El requisito actual de elegir operador por envío se conserva. |
| §3.4, p. impresa 3 (PDF p. 4) | Zona de cobertura y franja horaria aparecen como entidades sugeridas, no obligatorias ni exhaustivas. | Son una propuesta de modelado, no un esquema prescrito por el PDF. |
| §4, p. impresa 3 (PDF p. 4) | El Backoffice ASP.NET Core Razor Pages administra zonas y franjas. | Se reutilizan Backoffice, API y el ApiClient existentes; no se crea otra aplicación de mapas. |
| §5.1.2, p. impresa 3 (PDF p. 4) | Definición de zonas de cobertura y de las franjas horarias de entrega ofrecidas en cada zona. | Es el alcance funcional directo. No especifica formato geográfico, Mapbox, días semanales ni reglas de solapamiento. |
| §5.1.3 y §5.2.9, pp. impresas 3–4 (PDF pp. 4–5) | Las tarifas varían al menos por zona y la tarifa se calcula al dar de alta un envío con el cuadro vigente. | La integración tarifaria debe recibir una zona inequívoca, pero el cálculo tarifario permanece en CU-03/CU-09. |
| §§5.2.7–5.2.9, p. impresa 4 (PDF p. 5) | CU-07 registra envíos, admite carga individual/archivo/API e importa idempotentemente; el cálculo tarifario ocurre al alta. | CU-02 no modifica alta, importación ni precio. Un contrato futuro podrá consultar una cobertura al recibir coordenadas, si CU-07 incorpora esa búsqueda. |
| §5.3, requisito 14, p. impresa 4 (PDF p. 5) | La ruta debe ser compatible con las franjas comprometidas. | CU-13/CU-14 consumen una promesa concreta, no solo la oferta semanal mutable. |
| §6.4, p. impresa 5 (PDF p. 6) | Las entidades susceptibles de edición simultánea requieren concurrencia optimista demostrable. | La zona es el agregado de concurrencia; toda edición de una franja también cambia la versión de zona. |
| §6.5, p. impresa 6 (PDF p. 7) | Exige aislamiento multi-inquilino efectivo, resolución de tenant en ejecución y una demostración con dos operadores distintos. | Se reutilizan contexto autenticado persistido y RLS por OperadorId; se demuestra con operadores que se solapan territorialmente y tienen horarios diferentes. |
| §6.6, p. impresa 6 (PDF p. 7) | Tarifas y reglas operativas se editan en caliente y se versionan; envíos anteriores conservan el tratamiento. | El texto nombra expresamente tarifas y reglas, no zonas. El historial completo de geometría/franjas se propone por la decisión de diseño acordada y el criterio de cierre M01 del XLSM. |

### Planilla de responsables

Fuente obligatoria: [Casos de Uso - Responsables.xlsm](../../../docs/alcance/calcs/Casos%20de%20Uso%20-%20Responsables.xlsm). Se inspeccionó el XLSM leyendo las hojas y celdas del archivo; no se ejecutaron macros. El paquete no contiene una parte vbaProject. Las hojas reales son Módulos, CU obligatorios, Alcance aplicaciones, Dependencias, No funcionales y Opcionales; no existe una hoja llamada Responsables.

| Hoja/rango | Registro comprobado |
|---|---|
| CU obligatorios!A5:I5 | CU-02, fuente 2, Zonas y franjas; descripción completa: “Definir zonas de cobertura y franjas de entrega ofrecidas por zona.”; módulo M01; §5.1; superficie Backoffice; actor “Operador / administrador”; obligatorio. La fila no tiene campo de responsable individual. |
| Módulos!A4:F4 | M01 Administración y configuración; incluye operadores, comercios, usuarios, zonas, franjas, tarifas, reglas y marca; aplicaciones “Backoffice Razor Pages; API”; criterio de cierre “Configuración distinta para dos operadores, editable sin despliegue y con historial de versiones.” Responsable del módulo: José Garcia. La asignación es de M01, no una asignación explícita de CU-02. |
| Dependencias!A7:I7 | D04: CU-13 (M03) depende de CU-02 (M01), tipo Datos. Planificación necesita zona y ventana comprometida; contrato: zona y franja del envío; riesgo: ruta incompatible con la promesa. |
| Dependencias!A10:I10 | D07: CU-14 (M03) depende de CU-02 (M01), tipo Regla/datos. Las restricciones de franja dependen de calendario y ventanas configuradas; riesgo: compromisos incumplibles. |
| CU obligatorios!A10:I10 y A12:I12 | CU-07 es alta de envíos de Comercio en Portal/API; CU-09 calcula tarifa al alta según el cuadro vigente. Sirven para separar esta configuración de su consumo. |

**Diferencias que el equipo debe revisar antes de integrar:**

1. La decisión acordada precisa que el comercio elige operador en cada envío. El PDF solo afirma que un comercio puede trabajar con más de un operador; no exige un vínculo permanente. El modelo actual de Envio.OperadorId por envío respalda la decisión acordada.
2. Mapbox, PostGIS, edición dibujada, límites departamentales, zona horaria, política de superposición y reglas semanales no están en la descripción de CU-02 del PDF/XLSM: son decisiones de este diseño, no citas del requisito.
3. §6.6 del PDF habla explícitamente de tarifas y reglas operativas; el criterio de cierre de M01 extiende la versionabilidad a la configuración del módulo. Este diseño conserva zonas/franjas históricas por la dirección acordada, sin atribuir esa ampliación literalmente al §6.6.
4. La planilla registra CU-13 y CU-14 como consumidores de CU-02, pero no una dependencia de CU-09 aunque el PDF requiere tarifas por zona. Antes de integrar precio, acordar si CU-09 recibirá un zonaId resuelto por CU-07 y cómo se resolverá una ubicación cubierta por más de una zona del mismo operador. CU-02 no calcula tarifas.
5. No hay responsable específico de CU-02 en la fila de la planilla; José Garcia figura como responsable del módulo M01.

## Relevamiento inicial del código (evidencia histórica)

La columna «Falta para CU-02» registra las necesidades identificadas antes de implementar el caso. No es una lista de pendientes actuales; la implementación y su evidencia se describen en las secciones posteriores.

| Área | Existe y se reutiliza | Falta para CU-02 |
|---|---|---|
| Operador/Comercio | [Operador.cs](../../src/NexoRuta.Domain/Administracion/Operador.cs) y [Comercio.cs](../../src/NexoRuta.Domain/Administracion/Comercio.cs) tienen Id: Guid, nombre, estado y Version: long. Comercio no tiene OperadorId. | Agregado y configuración de cobertura, sin introducir membresía comercio–operador. |
| Envío/dirección | [Envio.cs](../../src/NexoRuta.Domain/Envios/Envio.cs) guarda operador, comercio, usuario, destinatario, dirección y estado. [Direccion.cs](../../src/NexoRuta.Domain/Envios/Direccion.cs) guarda Id, OperadorId y Descripcion; no tiene latitud/longitud ni geometría. [CrearEnvioRequest.cs](../../src/NexoRuta.Api/Features/Envios/Contracts/Requests/CrearEnvioRequest.cs) acepta operador seleccionado por envío y dirección textual. | Coordenadas/geocodificación no existen; CU-02 recibe coordenadas solo para consulta de cobertura y no geocodifica texto. |
| Propiedad por envío | [CrearEnvioUseCase.cs](../../src/NexoRuta.Application/Envios/UseCases/CrearEnvioUseCase.cs) obtiene contexto de Comercio y crea cada envío con el operador seleccionado en esa operación. [NexoRutaDbContext.cs](../../src/NexoRuta.Infrastructure/Persistence/NexoRutaDbContext.cs) vincula hijos de envío mediante claves compuestas por operador. | No crear una asociación permanente Comercio–Operador. Una integración de CU-07 conserva la elección individual por envío. |
| Identidad/permisos | [IUsuarioActual.cs](../../src/NexoRuta.Application/Administracion/Interfaces/IUsuarioActual.cs), [ContextoUsuario.cs](../../src/NexoRuta.Application/Administracion/Context/ContextoUsuario.cs), [UsuarioActualHttp.cs](../../src/NexoRuta.Api/Core/Security/UsuarioActualHttp.cs) y [PermisoAuthorizationHandler.cs](../../src/NexoRuta.Api/Core/Security/PermisoAuthorizationHandler.cs) ya resuelven al usuario, acceso persistido y permisos dinámicos. El catálogo [CatalogoPermisos.cs](../../src/NexoRuta.Domain/Administracion/CatalogoPermisos.cs) contiene configuracion.gestionar para Operador y Comercio. | No agregar otro mecanismo de permiso. Los endpoints de CU-02 requieren configuracion.gestionar y tipo de actor Operador; el permiso solo no debe habilitar Comercio. |
| Concurrencia | [NexoRutaDbContext.cs](../../src/NexoRuta.Infrastructure/Persistence/NexoRutaDbContext.cs) marca versiones long como concurrency tokens; [OrganizacionPropiaController.cs](../../src/NexoRuta.Api/Features/Organizaciones/Controllers/OrganizacionPropiaController.cs) devuelve 409 ante DbUpdateConcurrencyException. | Aplicar el mismo patrón a la zona y usar la versión del agregado para toda edición de franjas. |
| RLS/contexto | [RlsTransactionMiddleware.cs](../../src/NexoRuta.Api/Core/Security/RlsTransactionMiddleware.cs) vuelve a consultar acceso, estado y sesión persistidos antes de procesar; [RlsTransaction.cs](../../src/NexoRuta.Infrastructure/Persistence/RlsTransaction.cs) establece el actor en contexto transaccional local. La [guía RLS](../../../docs/rls/README.md) y [auditoría RLS](../../../docs/rls/AUDITORIA.md) describen roles runtime restringidos, ENABLE/FORCE, grants específicos y aislamiento por fila. | Nuevas tablas y políticas propias de CU-02, reutilizando RlsActorContext y el OperadorId persistido. |
| Capas | NexoRuta.Domain, Application, Infrastructure y Api ya están separados; la infraestructura EF registra el modelo y migraciones. | Agregar CU-02 a esas capas existentes; no abrir un segundo contexto de tenant ni otro API. |
| Backoffice/ApiClient | [NexoRuta.Backoffice/Program.cs](../../../frontend/src/NexoRuta.Backoffice/Program.cs) configura Razor Pages, políticas dinámicas y SesionUsuario.Operador. [NexoRuta.ApiClient](../../../frontend/src/NexoRuta.ApiClient) organiza contratos y clientes tipados por feature. El layout concentra navegación y las páginas usan PageModel. | Páginas Razor y un ApiClient tipado para zonas/franjas; el mapa se integra en una página existente del Backoffice. |
| EF/espacial | [NexoRutaDbContext.cs](../../src/NexoRuta.Infrastructure/Persistence/NexoRutaDbContext.cs), sus [migraciones](../../src/NexoRuta.Infrastructure/Persistence/Migrations/20261017201732_EnablePostgreSqlRls.cs) y [snapshot](../../src/NexoRuta.Infrastructure/Persistence/Migrations/NexoRutaDbContextModelSnapshot.cs) mantienen el modelo PostgreSQL. [NexoRuta.Infrastructure.csproj](../../src/NexoRuta.Infrastructure/NexoRuta.Infrastructure.csproj) usa Npgsql EF 10.0.3. | No hay paquete NetTopologySuite, mapping espacial, migración PostGIS ni geometrías en el snapshot actual. |
| PostgreSQL/Mapbox | [compose.yaml](../../../compose.yaml) usa PostgreSQL 18 Alpine, no una imagen con PostGIS. La inspección de proyectos no encontró integración, paquete ni clave Mapbox existente; no se incorporan valores de configuración ni credenciales en el diseño. | Imagen PostGIS compatible con PG18, extensión PostGIS, Npgsql NetTopologySuite y configuración pública de Mapbox para el navegador. |

## Alcance funcional y recorrido

### Operaciones incluidas

- Alta de zona dibujada o iniciada desde un departamento de referencia.
- Listado del operador autenticado con filtros por activa/inactiva.
- Consulta de una zona, sus franjas y número de revisión.
- Edición de nombre y geometría.
- Alta, listado, consulta y edición de franjas de una zona.
- Activación/desactivación explícita de zona y franja; no se borra físicamente configuración.
- Consulta diagnóstica de cobertura por longitud/latitud dentro del operador autenticado.

### Recorrido principal en Backoffice

1. El operador autorizado abre “Zonas y franjas” en el menú del Backoffice.
2. Elige dibujar una zona o selecciona un departamento del catálogo oficial importado. La selección carga la geometría disponible localmente; no descarga un polígono en cada petición.
3. Ajusta el polígono en el mapa, asigna nombre y configura día/hora de cada franja. Puede revisar la geometría y el calendario antes de publicar.
4. Guarda; la API valida la geometría y las franjas, verifica la versión enviada y guarda zona, filas de franja y snapshot histórico en una sola transacción.
5. Activa la zona cuando ya tiene al menos una franja activa sin conflictos. Luego puede consultar el detalle, desactivarla o volver a activarla.
6. Para demostrar cobertura, ingresa longitud y latitud en la consulta de prueba. El resultado lista todas las zonas activas propias y sus ofertas semanales; Commerce no necesita estar conectado al formulario de alta para esta demostración.

## Modelo de dominio

~~~mermaid
erDiagram
    Operador ||--o{ ZonaCobertura : posee
    ZonaCobertura ||--o{ FranjaEntrega : ofrece
    ZonaCobertura ||--o{ ZonaCoberturaRevision : conserva
~~~

`CodigoDepartamento` y `VersionFuente` son procedencia, no una FK ni propiedad del catálogo. Una zona conserva su geometría independiente aunque cambie la referencia oficial o la tabla de referencias esté vacía.

| Entidad/tabla | Campos y tipos propuestos | Función |
|---|---|---|
| ZonaCobertura | Id uuid PK; OperadorId uuid NOT NULL; Nombre varchar(120) NOT NULL; EstadoZona (Activa, Inactiva); Geometria geometry(MultiPolygon,4326) NOT NULL; ZonaHoraria varchar(64) NOT NULL; Version bigint NOT NULL; RevisionActual integer NOT NULL; OrigenGeometria (Dibujada, Departamento); CodigoDepartamento varchar(16) NULL; VersionFuente varchar(80) NULL; CreadaUtc/ActualizadaUtc timestamptz. Clave alterna UNIQUE (OperadorId, Id) y FK de operador. | Estado actual editable, geometría de propiedad del operador y concurrencia del agregado. ZonaHoraria comienza como America/Montevideo; se guarda para interpretar el calendario histórico. OrigenGeometria y código/versión de fuente guardan procedencia opcional sin FK al catálogo. |
| FranjaEntrega | Id uuid PK; OperadorId uuid NOT NULL; ZonaId uuid NOT NULL; DiaSemanaIso smallint NOT NULL (1 lunes…7 domingo); HoraInicio time NOT NULL; HoraFin time NOT NULL; EstadoFranja (Activa, Inactiva). FK compuesta (OperadorId, ZonaId) -> ZonaCobertura(OperadorId, Id). | Oferta semanal recurrente asociada a exactamente una zona del mismo operador. No tiene versión propia: cualquier cambio de franja incrementa la versión de la zona y crea una revisión del conjunto. |
| ZonaCoberturaRevision | OperadorId uuid; ZonaId uuid; NumeroRevision integer; RegistradaUtc timestamptz; UsuarioId uuid; Snapshot jsonb NOT NULL; PK (OperadorId, ZonaId, NumeroRevision); FK compuesta al propietario/zona. | Fotografía inmutable de nombre, estado, GeoJSON canónico, zona horaria, procedencia y todas las franjas/estados en esa revisión. Se inserta en la misma transacción que la modificación. |
| DepartamentoReferencia | Codigo varchar(16), VersionFuente varchar(80), PK compuesta (Codigo, VersionFuente); Nombre varchar(100); Geometria geometry(MultiPolygon,4326); UrlFuente varchar(500); Atribucion varchar(300); Licencia varchar(160); ImportadaUtc timestamptz. | Catálogo compartido y opcional para la lista persistida/operación de importación. La versión conserva ediciones de referencia. Ni las zonas ni la validación territorial dependen de que existan filas aquí. |

**Aislamiento de franjas.** En aplicación, el caso de uso obtiene el operador desde IUsuarioActual, carga la zona dentro de ese alcance, y valida que su propietario coincida antes de mutarla. En PostgreSQL, la FK compuesta (OperadorId, ZonaId) impide que una franja del operador A apunte a una zona del operador B aunque un defecto de aplicación intente hacerlo. El mismo patrón se usa para revisiones.

## Reglas de geometría y cobertura

- **CRS:** WGS 84, SRID 4326. API e intercambio usan GeoJSON RFC 7946. Cada posición es [longitud, latitud]; con Npgsql/NetTopologySuite se mapea X a longitud e Y a latitud.
- **Tipos:** aceptar Polygon o MultiPolygon; normalizar y persistir siempre como MultiPolygon. Se conservan agujeros e islas de polígonos multipartes. No se impone que una zona coincida con un departamento ni se recortan zonas dibujadas a límites departamentales.
- **Validación:** rechazar JSON mal formado, tipo distinto, geometría vacía, coordenadas no finitas o fuera de longitud −180…180 / latitud −90…90, y geometría 3D en este contrato 2D. Validar anillos cerrados y usar ST_IsValid (incluye autointersecciones); dominio y base imponen 2D. No ejecutar ST_MakeValid silenciosamente: podría cambiar la cobertura que el operador dibujó. Devolver el error de geometría como validación accionable, sin loguear el GeoJSON completo.
- **Superposición:** zonas del mismo operador pueden superponerse; zonas de operadores distintos también. El sistema no las fusiona ni decide prioridad. Si un punto cae en dos zonas activas del mismo operador, la respuesta contiene ambas y declara requiereSeleccionDeZona=true; no toma la primera fila ni la zona “más pequeña”.
- **Bordes:** usar ST_Covers(zona, punto), que considera cubierto un punto en el interior o exactamente en el límite. Si dos zonas comparten ese borde, ambas aparecen en coincidencias.
- **Sin cobertura:** responder resultado válido sin coincidencias (estado=SinCobertura, lista vacía), no un error de servidor ni una zona inventada.
- **Desactivación:** una zona inactiva no participa en consultas y ninguna de sus franjas se ofrece, aunque sus filas sigan activas. Las franjas conservan sus propios estados para que reactivar la zona restaure solo las ofertas que seguían activas. Una zona solo se puede activar con al menos una franja activa.
- **Consulta por tenant:** esta consulta diagnóstica se ejecuta para el operador del contexto autenticado. No recibe OperadorId del cliente y no consulta operadores ajenos. Un futuro buscador de operadores disponibles para Comercio es otro contrato, probablemente de CU-07.

### Ejemplo de coincidencia

El punto de prueba [-56.1645, -34.9011] es solamente una coordenada de consulta, no un límite ni una fuente para crear polígonos. Los operadores O-17 y O-42 tienen geometrías PostGIS distintas que coinciden en ese punto: O-17 ofrece lunes–viernes 09:00–12:00 y O-42 ofrece lunes–viernes 14:00–18:00. La consulta en la sesión de O-17 retorna únicamente su zona y horario; la sesión de O-42 retorna únicamente los suyos. Si O-17 dibuja dos zonas superpuestas con horas diferentes, su consulta devuelve las dos coincidencias y el operador debe resolver la zona, nunca se prioriza una arbitrariamente.

## Franjas y calendario

- El calendario es semanal y recurrente. DiaSemanaIso usa ISO-8601: lunes=1 hasta domingo=7. Horas son hora local en la zona horaria IANA almacenada para la zona; no son UTC ni una promesa de fecha concreta.
- Cada franja representa intervalo semiabierto [inicio, fin). Si HoraFin > HoraInicio, termina el mismo día; si HoraFin < HoraInicio, cruza medianoche y termina al día siguiente. HoraFin == HoraInicio es inválido (no significa 24 horas). Ejemplo: viernes 22:00–02:00 termina sábado a las 02:00.
- HoraInicio y HoraFin se conservan con precisión máxima de microsegundos (PostgreSQL `time(6)`); valores submicrosegundo se rechazan en dominio para que comparación, almacenamiento e historial representen el mismo calendario.
- Dos franjas activas de una misma zona no pueden solaparse, incluyendo los minutos del día siguiente de una franja nocturna. Intervalos contiguos sí son válidos: 09:00–12:00 y 12:00–15:00. Al activar una franja se vuelve a comprobar el calendario completo. Una fila inactiva no ofrece servicio y no bloquea otra activa.
- Una zona inactiva no ofrece franjas; desactivar una franja solo retira esa oferta de la zona, sin borrarla. No se permite dejar activa una zona sin al menos una franja activa: primero se desactiva la zona y luego, si se desea, sus franjas.
- No hay feriados, calendario por fecha, capacidad, cupos, reservas ni bloqueo de disponibilidad en CU-02.
- Una franja semanal es una oferta recurrente, no una ventana comprometida. Cuando otro caso acepte una promesa para fecha concreta debe resolver la fecha en America/Montevideo y guardar los instantes/offsets concretos; un cambio posterior del horario semanal no reescribe compromisos existentes.
- Si una futura regla horaria hace una hora local inexistente por cambio de zona/TZDB, esa franja no se ofrece para esa fecha. Si la hora local es ambigua, se toma el primer instante válido para el inicio y el último para el fin, cubriendo las dos ocurrencias del intervalo repetido. La promesa concreta guarda sus offsets e instantes; hoy Uruguay usa America/Montevideo y CU-02 no genera compromisos.

## Historial y concurrencia

Son mecanismos diferentes y ambos son necesarios:

1. **Concurrencia optimista:** ZonaCobertura.Version: long actúa como token de concurrencia, siguiendo el patrón Version existente. Cada escritura incluye la versión que leyó el cliente. Un cambio de zona, franjas o estado incrementa Version y RevisionActual; la actualización SQL usa la versión previa. Si alguien guardó primero, la segunda escritura revierte y responde HTTP 409 para recargar y reaplicar deliberadamente.
2. **Historia:** cada escritura aceptada inserta una nueva fila ZonaCoberturaRevision con un snapshot completo inmutable. El contador no guarda geometría ni horarios anteriores. La tabla de revisiones no admite UPDATE/DELETE para el rol runtime de API.
3. **Atomicidad:** el cambio de zona/franjas, nuevo número de revisión, fila de snapshot y versión actual se escriben en la misma transacción PostgreSQL. Un conflicto no deja el snapshot separado ni la configuración parcial.
4. **Envío futuro:** al concretarse una promesa, CU-07 u otro caso deberá conservar al menos ZonaCoberturaId, número/ID de revisión inmutable, FranjaEntregaId si aplica, fecha local, día/zona IANA, hora local prometida y los instantes UTC/offset finalmente aceptados. Conviene mantener FK compuesta a (OperadorId, ZonaId, Revision) y copiar la ventana concreta al registro de envío para que el significado persista aun si cambia el TZDB o la configuración actual.
5. **Límite de CU-02:** no se agregan esos campos al Envio, no se modifica su alta y no se calcula la promesa. CU-07 debe decidir cómo una dirección pasa a coordenadas y cómo resuelve varias zonas; CU-09 consume la zona para tarifa; CU-13/CU-14 comprueban ruta contra la ventana ya comprometida.

## Autorización y RLS

configuracion.gestionar es la capacidad existente y es suficiente para operar esta configuración; no se propone zonas.gestionar. Dado que el catálogo vigente también permite ese permiso a Commerce, CU-02 debe comprobar además TipoAccesoUsuario.Operador. Es una restricción del caso sobre el permiso existente, no un segundo esquema de autorización.

El operador efectivo viene del acceso autenticado revalidado y persistido por el middleware. No se acepta OperadorId de formulario, ruta, query ni DTO para establecer propietario. El ID de zona tampoco evita RLS: un ID ajeno se consulta en el contexto propio y no revela si existe.

| Actor | Consultar propias zonas/cobertura | Crear/editar/activar/desactivar | Acceso a otra organización |
|---|---|---|---|
| Operador con configuracion.gestionar | Sí, solo su tenant | Sí | No |
| Usuario del operador sin permiso | 403 | 403 | No |
| Usuario de otro operador | Sí, solo sus propias zonas si tiene permiso | Sí, solo su tenant y permiso | 404 para un ID ajeno; no se confirma su existencia |
| Comercio | 403 para la API administrativa y consulta diagnóstica | 403 | No ve configuración del operador |
| Plataforma | Sin acceso operativo CU-02 | No | No hay lectura global ni administración de configuración |
| Usuario autenticado sin organización operativa | 403 | 403 | No |
| Anónimo o sesión inválida | 401 | 401 | No |

Políticas aplicadas a las tablas de esta entrega:

- ZonasCobertura y FranjasEntrega: ENABLE ROW LEVEL SECURITY y FORCE ROW LEVEL SECURITY; permisos SQL mínimos a nexoruta_api; SELECT/UPDATE solo cuando actor_kind=operador y OperadorId=nexoruta_app.ctx_uuid('org_id'); INSERT WITH CHECK con el mismo predicado; sin borrado físico runtime.
- ZonaCoberturaRevision: mismo filtro de operador; API puede leer e insertar snapshots propios, nunca actualizar/eliminar. Escritura del snapshot debe quedar ligada a la actualización del agregado.
- DepartamentoReferencia: dato de referencia compartido, no información operacional de tenant. El grupo API recibe únicamente SELECT con política acotada al actor operador; nada de INSERT/UPDATE/DELETE para API ni lectura por plataforma/comercio. Importación/versionado se hace por el proceso autorizado de inicialización/migración, no descargando desde la petición.
- FK compuesta de franja/revisión a zona evita cruce de propietario a nivel de base aun si el predicado de aplicación falla.
- No se concede SELECT general ni bypass RLS a API o workers. El propietario es nexoruta_schema_owner, rol NOLOGIN/NOBYPASSRLS; no se añadió otra función SECURITY DEFINER. Se reutilizan las funciones de contexto RLS existentes y el trigger de inmutabilidad tiene search_path fijo y EXECUTE revocado a PUBLIC y roles runtime.
- Las pruebas enfocadas ejecutan consultas con un login restringido que hereda nexoruta_api: operador propio, otro operador, Commerce, Plataforma y sin contexto; prueban inserción válida/cruzada, FK compuesta y privilegios de API/worker.

## Contratos HTTP implementados

El prefijo real es `/api/configuracion/zonas`. Cada operación aplica las políticas existentes `organizacion` y `configuracion.gestionar`; el caso de uso valida además el acceso persistido activo de tipo Operador. La API obtiene el `OperadorId` de `IUsuarioActual` y reutiliza la transacción RLS del middleware. Commerce, Plataforma y usuarios autenticados sin organización operativa reciben 403 aunque tengan un permiso con el mismo código; anónimos reciben 401. IDs que la RLS no permite ver, incluidos los de otro operador, responden 404.

| Operación | Entrada | Respuesta y comportamiento |
|---|---|---|
| `GET /api/configuracion/zonas?estado=Activa|Inactiva&pagina=1&tamanoPagina=20` | Filtro de estado opcional y paginación. Estado desconocido se rechaza; página 1–10.000 y tamaño 1–100. | 200 con resumen sin geometría, total y paginación. Por defecto filtra `Activa`; orden estable por ID. |
| `GET /api/configuracion/zonas/{zonaId}` | UUID de la zona. | 200 detalle con geometría GeoJSON como objeto JSON, estado, zona horaria, franjas, versión y revisión vigente; 404 si no existe en el tenant. |
| `POST /api/configuracion/zonas` | `{ nombre, geometria }` o `{ nombre, codigoDepartamento }`, exactamente una fuente. GeoJSON `Polygon` o `MultiPolygon`, coordenadas longitud/latitud. Cuerpo máximo 1 MiB. | 201 con `Location`; la zona nace inactiva y queda normalizada como `MultiPolygon` SRID 4326. No acepta propietario, versión ni revisión. Código departamental ausente del catálogo: 404. |
| `PUT /api/configuracion/zonas/{zonaId}` | `{ nombre, geometria, version }`. Versión obligatoria, incluido valor cero. Cuerpo máximo 1 MiB. | 200 detalle actualizado. Nombre y geometría componen una sola mutación, incremento y revisión. Versión obsoleta: 409 sin escritura parcial. |
| `PUT /api/configuracion/zonas/{zonaId}/estado` | `{ estado: "Activa"|"Inactiva", version }`. | 200 detalle. Activar exige una franja activa; valor inválido 400 y conflicto vigente 409. |
| `GET /api/configuracion/zonas/{zonaId}/franjas` | UUID de zona. | 200 con franjas activas/inactivas y versión/revisión del agregado; 404 si la zona no es visible. |
| `POST /api/configuracion/zonas/{zonaId}/franjas` | `{ diaSemanaIso, horaInicio, horaFin, versionZona }`; día ISO 1=lunes…7=domingo; hora local America/Montevideo, precisión máxima de microsegundos; versión admite cero. | 201 con franja y nueva versión/revisión. Una franja nocturna usa hora final anterior a la inicial. Solapamientos, incluido domingo–lunes, dan 409. |
| `PUT /api/configuracion/zonas/{zonaId}/franjas/{franjaId}` | Día, horas, estado `Activa`/`Inactiva` y versión de zona. | 200 con franja y nueva versión/revisión; franja no perteneciente a esa zona devuelve 404. Desactivar la última franja de zona activa da 409. |
| `GET /api/configuracion/zonas/departamentos` | Sin parámetros. | 200 con metadatos de referencia local vigente; sin importación devuelve `[]`. No descarga datos externos. |
| `POST /api/configuracion/zonas/cobertura` | `{ longitud, latitud }` en WGS 84. | 200 `SinCobertura` con lista vacía, o `Cubierta` con todas las coincidencias activas del operador y sus revisiones/franjas activas. `requiereSeleccionDeZona` es true si hay varias; nunca elige una. |

Las horas se intercambian como strings ISO de hora local (por ejemplo `23:30:00.123456`). Se conserva precisión de PostgreSQL `time(6)`; el backend rechaza fracciones inferiores al microsegundo. `version` y `versionZona` son obligatorias aunque valgan 0. Los DTOs mutables rechazan propiedades JSON desconocidas. La geometría se limita por tamaño de cuerpo (1 MiB); el validador espacial existente verifica tipo, SRID, dimensión, rangos y validez, y los errores no repiten ni registran el contenido GeoJSON.

Errores: 400 para contrato, estado, coordenadas, geometría, horario o paginación inválidos; 401 autenticación ausente/inválida; 403 identidad autenticada sin contexto Operador activo o sin permiso vigente; 404 recurso/código departamental no visible o inexistente; 409 versión obsoleta, solapamiento activo o regla de activación. La validación automática del DTO devuelve `ValidationProblemDetails` con `errors`; los errores de reglas de aplicación usan el JSON existente `{ message }`. No se exponen IDs ajenos ni la geometría enviada.

El ApiClient tipado agrega `ZonasCoberturaApiClient` y contratos propios en `frontend/src/NexoRuta.ApiClient/Features/ZonasCobertura`. Reusa `ApiHttp`, Bearer y el formato de error existente; el tipo quedó registrado en Backoffice durante el corte de API, antes de implementar las páginas y el mapa documentados más adelante. `frontend/contracts/nexoruta.openapi.json` refleja las rutas, esquemas y respuestas generadas por la API. GeoJSON se serializa como objeto JSON y no como string escapado.
Verificación de esta entrega (2026-10-18): las pruebas unitarias enfocadas de ZonasCobertura y ApiClient/OpenAPI, y los builds Release de API, IntegrationTests y Backoffice, constan como aprobados en la verificación previa. En esta pasada se corrigió la liberación parcial de `PostgresFixture`: `PostgreSqlBuilder.Build()` puede fallar antes de asignar el contenedor (su validación intenta contactar Docker); la disposición ahora acepta `container == null`, y un fallo al liberar tras un `StartAsync()` fallido no sustituye la excepción original. Se conserva el error original y se adjunta únicamente el tipo de fallo secundario de limpieza.

Diagnóstico de Docker/Testcontainers (2026-10-18): `docker version --format ...` devolvió cliente 28.0.4 y ningún servidor; `docker info --format ...` tampoco pudo consultar servidor. El contexto actual `desktop-linux` apunta a `npipe:////./pipe/dockerDesktopLinuxEngine`; el contexto `default` apunta a `npipe:////./pipe/docker_engine`. Ambos endpoints faltaban en la enumeración de pipes. `docker desktop status` indicó que no podía recuperar el estado, aunque había procesos Docker Desktop; `com.docker.service` estaba detenido. WSL informó Ubuntu-22.04 como distribución predeterminada, pero al no responder el servidor no se pudo comprobar el soporte efectivo de contenedores Linux. No había variables de proceso `DOCKER_*`/`TESTCONTAINERS_*` ni propiedad `docker.host` en `~/.testcontainers.properties`. Testcontainers intentó su endpoint predeterminado `npipe://./pipe/docker_engine`, distinto del contexto de Docker CLI; sin embargo, también estaba ausente. La causa confirmada es que el motor/backend de Docker Desktop no estaba disponible; además existe una diferencia entre el endpoint del contexto seleccionado y el predeterminado de Testcontainers. No se aplicó override de proceso porque ninguno de los endpoints comprobados era accesible; no se modificaron variables globales ni configuración de Docker.

Prueba enfocada ejecutada: `dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --filter "FullyQualifiedName~ZonaCoberturaPostgisTests.MigrationFromEmptyDatabasePersistsPostgisZonesSlotsAndImmutableHistory" --no-restore --logger "console;verbosity=minimal"` compiló los proyectos dependientes, pero 1 prueba falló en `PostgreSqlBuilder.Build()` antes del cuerpo de prueba con `DockerUnavailableException`/timeout en `npipe://./pipe/docker_engine`. Tras el cambio de fixture no apareció `NullReferenceException`; la inicialización falla por ausencia del endpoint, y la limpieza sin contenedor es segura. Testcontainers no llegó a crear el contenedor, por lo que no se descargó la imagen ni se creó una base de prueba: no se usó almacenamiento efímero en esta ejecución. No se tocó PostgreSQL persistente de Compose, Compose ni servicios locales.

Pendiente al cierre del corte del 2026-10-18; resuelto el 2026-10-19: Docker volvió a responder y se ejecutaron las pruebas PostGIS/HTTP enfocadas contra almacenamiento efímero. Ver «Puesta en funcionamiento local y verificación» abajo. La compilación sola no acredita esos recorridos.

## Mapbox, PostGIS y referencia geográfica

### Compatibilidad técnica

- El Compose quedó configurado con `postgis/postgis:18-3.6-alpine`, compatible con PostgreSQL 18. En el corte siguiente (2026-10-19) se verificó el volumen/ruta y se aplicó la imagen sobre el PostgreSQL local existente sin cambiar de versión mayor; el detalle y la migración PostGIS están documentados abajo.
- La integración espacial está implementada con EF Core 10.0.12, Npgsql EF 10.0.3 y `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` 10.0.3. Una extensión de opciones compartida configura `UseNetTopologySuite()` para runtime, design-time y tests. El modelo restringe geometrías a `geometry(MultiPolygon,4326)` y franjas horarias a `time(6) without time zone`.
- PostGIS ejecuta validación y predicados espaciales en PostgreSQL; el índice GiST sobre geometría soporta la consulta. Para longitud/distancia no incluida en CU-02 no se cambia a geography.
- Mapbox GL JS dibuja el mapa; el dibujo se añade con mapbox-gl-draw, plugin aparte de GL JS. En la Razor Page existente se carga el mapa, se carga/edita GeoJSON con JS y el botón Guardar usa el ApiClient/API. El servidor es autoridad para validar y persistir.
- La configuración pública que necesitará el Backoffice puede llamarse Mapbox__PublicAccessToken y Mapbox__StyleUrl. La clave de acceso de navegador es pública y visible al cliente: restringir por URL/origen y uso; no tratarla como secreto de servidor ni inspeccionar valores en esta pasada.
- La aplicación usa la atribución cartográfica que exija Mapbox y atribución IDEUy/IGM al mostrar límites oficiales. No se usa inicialmente Mapbox Boundaries comercial.

### Fuente departamental propuesta inicialmente (antecedente histórico)

Esta sección conserva la investigación inicial de IDEUy. El artefacto efectivamente importado procede del servicio SIT del MVOT, con atribución IGM, y se identifica por su checksum en «Catálogo departamental y ámbito Uruguay». No se atribuyen a ese archivo la edición 2022 ni la licencia de otra distribución por semejanza de nombres. El proceso propuesto a continuación es histórico; prevalece el importador implementado y documentado al final.

La fuente oficial concreta disponible con metadatos completos es el registro IDEUy de [Límites departamentales IGM, edición identificada como 2022](https://visualizador.ide.uy/geonetwork/srv/api/records/bb9e4263-6206-4cd7-aa9e-0ff5144b4fe4). El registro identifica la capa INE_NO_SEGURO_CLON_NS:limites_departamentales_igm_20220211, fecha de publicación 2022-02-14, formato Shapefile 1.0, EPSG:4326 y derechos/licencia de Datos Abiertos Uruguay de AGESIC. Atribución propuesta: “Fuente: Instituto Geográfico Militar; publicado por IDE Uruguay/AGESIC; Límites departamentales, versión <fecha/identificador>”. La licencia se documenta en [Licencia de Datos Abiertos Uruguay](https://www.agesic.gub.uy/innovaportal/file/6327/1/licencia-de-datos-abiertos.pdf).

**Discrepancia de actualización:** al revisar el visualizador oficial el 18-10-2026, su catálogo presenta una capa más nueva rotulada “Límites Departamentales (IGM 18/08/2026)” ([IDEUy](https://visualizador.ide.uy/)). El registro de descarga con CRS/licencia completos citado arriba describe una publicación anterior. Antes de importar, el equipo debe comprobar que la edición 2026 tenga archivo descargable, CRS y licencia confirmados; no asumir esos metadatos por el rótulo del mapa. La propuesta es fijar una edición con artefacto y licencia comprobables y no actualizar zonas propias automáticamente.

Proceso de referencia previsto:

1. Descargar una edición aprobada una vez desde IDEUy, guardar junto al repositorio o artefacto de release el Shapefile con fuente, fecha, licencia, CRS y SHA-256 en un manifiesto de versión; no hacer consultas externas en cada request.
2. Importar en una operación de preparación controlada a DepartamentoReferencia, reproyectando a EPSG:4326 solo si el origen declarado fuera diferente; la edición 2022 declarada ya usa EPSG:4326. Normalizar geometrías a 2D MultiPolygon, verificar nombres/códigos y ST_IsValid, y rechazar/importar con informe los inválidos, no repararlos sin revisión.
3. La página Backoffice lista el catálogo local. Al tomar un departamento como inicio, el servidor copia su geometría a la nueva fila ZonaCobertura y conserva código/versión de procedencia; la copia queda bajo el operador, se edita de forma independiente y su historial almacena el polígono exacto usado.
4. El dato IDE es referencia compartida y de solo lectura. Las zonas dibujadas/derivadas son privadas del operador y quedan aisladas por RLS. Una nueva edición oficial actualiza solo el catálogo tras revisión; nunca muta una zona ni un compromiso histórico.

## Criterios de aceptación implementados y evidencia disponible

Los criterios siguientes describen el alcance implementado. La evidencia de backend, HTTP, RLS y contratos se registra en los resultados de las pruebas enfocadas; la confirmación visual manual del usuario se registra en el cierre. No se infiere que esa confirmación reprodujera individualmente cada comprobación técnica ni que exista automatización visual adicional.

- Un administrador Operador autorizado crea zona dibujada o copia una geometría departamental importada, agrega franjas y consulta el resultado en el Backoffice Razor Pages.
- Commerce no puede crear, editar ni consultar esta configuración mediante los endpoints administrativos; un OperadorId falsificado no cambia el propietario efectivo.
- Una franja con zona de otro operador falla por aplicación y también por FK compuesta de PostgreSQL; la consulta RLS de un operador no devuelve zonas ni franjas de otro.
- Geometría Polygon y MultiPolygon válida queda normalizada a MultiPolygon SRID 4326; GeoJSON usa longitud/latitud; polígonos vacíos, autointersectados o fuera de rango no se guardan.
- Una ubicación dentro de zonas activas de dos operadores puede ser cubierta por ambos sin compartir datos. Para un operador con zonas superpuestas, la respuesta lista ambas y no selecciona arbitrariamente.
- Un punto exactamente sobre el límite cuenta como cubierto; sin coincidencias se devuelve SinCobertura.
- Franjas contiguas se aceptan, solapamientos activos en la misma zona se rechazan, incluida una que cruza medianoche; cambios de día y desactivación no ofrecen horarios inexistentes.
- Activar una zona requiere al menos una franja activa. Desactivar una zona la quita de cobertura pero conserva zonas, franjas y revisiones.
- Dos ediciones con la misma versión producen una escritura exitosa y un 409 para la otra; ninguna deja una configuración o revisión parcial.
- Cada edición aceptada conserva snapshot completo de geometría, estado, zona horaria y franjas. La mutación del estado actual no altera snapshots ni promesas de envíos futuros.
- Ejecutado con roles API restringidos, RLS impide selección global, mutación de snapshot y cruce entre operadores; referencia departamental solo es legible por el flujo Operador y no editable por API.
- La demostración con O-17/O-42 muestra el mismo punto cubierto por ambos y horarios distintos; el resultado depende del contexto de cada operador.

## Estado de la base backend y API implementadas

Al cierre del corte backend/API/Backoffice del 2026-10-19, el catálogo aún no había sido importado. Ese estado es histórico y queda actualizado por la sección de importación/territorio al final. `ZonaCobertura` y sus `FranjaEntrega` forman un agregado: alta inactiva con franjas iniciales opcionales, cambios versionados, validación de solapamientos activos (incluida la semana domingo–lunes) y una revisión completa por escritura aceptada. La copia inicial de un departamento queda bajo propiedad del operador y conserva el par de procedencia `(CodigoDepartamento, VersionFuente)`; no hay relación muchos-a-muchos ni tabla de unión.

La configuración usa EF Core 10.0.12, Npgsql EF 10.0.3, `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` 10.0.3, `NetTopologySuite` 2.6.0 y `NetTopologySuite.IO.GeoJSON` 4.0.0; el builder espacial se comparte entre API, Worker, design-time e integration tests. `ZonaCobertura` y `DepartamentoReferencia` persisten `geometry(MultiPolygon,4326)`, validan SRID 4326, geometría 2D no vacía/válida y rangos WGS 84, y normalizan `Polygon` a `MultiPolygon`. Las franjas usan `time(6)`; los solapamientos se comparan en ticks completos (no truncados a minutos) y las revisiones serializan seis dígitos fraccionarios, evitando pérdida silenciosa de precisión admitida por PostgreSQL. La consulta interna usa `Covers`, devuelve todas las coincidencias activas del operador actual y las franjas activas; no elige una zona ni operador arbitrariamente.

La migración única `20261019010048_AddCoverageZonesAndDeliverySlots` crea PostGIS, las cuatro tablas y sus índices/relaciones/restricciones. Las tablas fuerzan RLS; su propietario es `nexoruta_schema_owner`, no los roles runtime. `nexoruta_api` recibe solo privilegios mínimos; las políticas de zona, franja e historial exigen `actor_kind=operador`, organización coincidente y, para insertar una revisión, usuario coincidente con el contexto RLS. El catálogo de referencia es de solo lectura y solo visible al actor Operador; API, workers y PUBLIC no pueden mutar el historial. El trigger de revisiones rechaza UPDATE/DELETE. La FK compuesta impide asociar una franja o revisión a otra organización.

En aquel corte `DepartamentoReferencia` estaba soportada pero aún vacía. La transición local controlada a `postgis/postgis:18-3.6-alpine` y la aplicación de la migración se documentan en la sección de verificación del 2026-10-19; la importación posterior está documentada al final. No se ejecutó `down -v` ni se borró ningún volumen.

Los casos de uso internos y los endpoints listados arriba reutilizan el contexto persistido, la capacidad dinámica y la transacción RLS existente. El ApiClient y snapshot OpenAPI están actualizados. Las rutas de lectura, edición, catálogo local y consulta geográfica no importan límites, no modifican envíos y no calculan tarifas. La interfaz Backoffice Razor Pages está implementada; la verificación real PostGIS/HTTP está aprobada con base efímera. En ese corte el recorrido visual dibujado con Mapbox estaba pendiente del token público local; esa limitación quedó superada por la configuración posterior y la validación manual del usuario registrada en el cierre.

## Dependencias reales y límites de CU-02

| Caso/módulo | Relación | Propiedad del trabajo futuro |
|---|---|---|
| CU-01 / M01 | Reusa identidad, pertenencia, roles y permisos existentes; conserva el propietario de la organización y sus restricciones. | CU-01 mantiene cuentas/permisos. CU-02 no agrega autorización paralela ni acceso global de Plataforma. |
| CU-03 y CU-09 | El PDF define tarifa por zona; la hoja no registra explícitamente la dependencia CU-09 → CU-02. | Acordar contrato de zona resuelta y manejo de zonas múltiples antes de integrar cálculo tarifario; no calcular precios aquí. |
| CU-07 / M02 | Alta individual/archivo/API pertenece a CU-07. El operador se elige por envío; dirección actual es texto sin punto geográfico. | CU-07 decidirá si geocodifica, cuándo consulta cobertura y qué hace ante múltiples operadores/zonas; guardará la zona/revisión/ventana concreta comprometida. No se toca en CU-02. |
| CU-13 / M03 | Dependencia XLSM D04: zona y ventana comprometida. | Planner asigna envíos a ruta respetando la ventana concreta, no una oferta semanal editada luego. |
| CU-14 / M03 | Dependencia XLSM D07: calendario y ventanas configuradas por zona. | Valida compatibilidad de las rutas con compromisos ya creados. |
| Dirección/geocodificación | El modelo solo almacena texto y no contiene coordenadas. | El caso que capture coordenadas debe decidir fuente/geocodificación y retención; CU-02 recibe un punto para su consulta sin modificar Direccion. |

No se agregan tarifas, planificación/optimización de rutas, carga de alta de envíos, geocercas, cupos, reservas, feriados, reprogramación ni un portal de configuración para Comercio.

### Puntos pendientes de revisión del equipo

1. Procedencia: el archivo ya importado es el GeoJSON MVOT/IGM identificado por checksum en la sección vigente. Su licencia y edición no están declaradas en la fuente consultada y siguen pendientes de confirmación; no se sustituyen por los metadatos de la propuesta inicial IDEUy. La importación y la validación funcional local ya están realizadas. El diseño no depende de Mapbox Boundaries ni de inferir polígonos desde puntos de ciudades.
2. Al iniciar integración de precio, decidir si la planilla registra formalmente la dependencia CU-09 → CU-02 y confirmar el contrato de zona para el cálculo de tarifas. No impide implementar ni demostrar CU-02 aisladamente.

## Referencias técnicas consultadas

- [RFC 7946 — GeoJSON, posiciones y orden de coordenadas](https://www.rfc-editor.org/info/rfc7946/).
- [PostGIS ST_Covers](https://postgis.net/docs/ST_Covers.html) — punto dentro o sobre el borde; [ST_IsValid](https://postgis.net/docs/manual-dev/en/ST_IsValid.html) — validez y autointersecciones.
- [Npgsql — Spatial Mapping with NetTopologySuite](https://www.npgsql.org/efcore/mapping/nts.html) — plugin, UseNetTopologySuite, tipo de columna y operaciones traducidas.
- [Imagen PostGIS oficial](https://hub.docker.com/r/postgis/postgis) — versiones disponibles y compatibilidad PostgreSQL 18.
- [Mapbox — Add drawing tools to a map](https://docs.mapbox.com/mapbox-gl-js/example/mapbox-gl-draw/) — Draw es un plugin separado de Mapbox GL JS.
- [IDEUy — registro de límites departamentales IGM](https://visualizador.ide.uy/geonetwork/srv/api/records/bb9e4263-6206-4cd7-aa9e-0ff5144b4fe4) y [visualizador oficial](https://visualizador.ide.uy/).

## Interfaz de gestión Backoffice implementada (2026-10-18)

La interfaz usa Razor Pages del Backoffice existente y el `ZonasCoberturaApiClient` ya registrado. No agrega cliente HTTP, JWT al navegador, autorización por nombre de rol, ni páginas de Commerce, Admin o Tracking.

### Rutas y comportamiento

- `GET /zonas`: listado de activas por defecto. El selector permite Activas, Inactivas o Todas, con tamaño 10/20/50 y paginación. El contrato API solo filtra un estado por petición; Todas consulta ambas categorías en la misma página y las muestra en secciones separadas. Cada resumen incluye nombre, estado, cantidad de franjas activas y revisión.
- `GET /zonas/crear` y `POST /zonas/crear`: nombre y exactamente una fuente (Dibujo o Departamento del catálogo local). El selector de departamento queda deshabilitado con catálogo vacío; el dibujo sigue disponible si Mapbox tiene configuración. El alta exitosa redirige a `/zonas/{zonaId}`, donde la zona creada se identifica como inactiva.
- `GET /zonas/{zonaId}`: detalle, estado, fuente/código/versión de referencia cuando están disponibles, zona horaria, versión de concurrencia y revisión separadas, mapa, franjas, y consulta visual de cobertura.
- Handlers POST del detalle: `Editar`, `Estado`, `CrearFranja`, `EditarFranja` y `ConsultarCobertura`. Las escrituras incluyen antiforgery y `data-single-submit`; el servidor llama al cliente tipado con el bearer de la sesión existente.

La navegación compartida y las páginas independientes (ingreso, recuperación, acceso denegado y API no disponible) muestran el enlace solo cuando las políticas existentes confirman contexto de Operador y permiso dinámico `configuracion.gestionar`. Commerce/Plataforma y el acceso suspendido no obtienen la herramienta. No se confía en `OperadorId` de formularios.

### Mapbox y fidelidad GeoJSON

- Se fijaron Mapbox GL JS `v3.33.0` y el plugin separado `mapbox-gl-draw v1.5.1`, concordantes con la guía y el ejemplo oficiales consultados. Mapbox Draw no viene incluido en GL JS; su API documenta soporte de `MultiPolygon`, pero el modo de dibujo de polígono genera piezas Polygon.
- El mapa inicializa centrado en Uruguay. Una o varias piezas dibujadas se serializan como `Polygon` o `MultiPolygon`; se envía objeto GeoJSON con coordenadas longitud/latitud. No se combinan ni descartan componentes dibujados.
- Para geometrías ya guardadas, solo se habilita edición por Draw si existe un único componente con un anillo exterior. Polygon/MultiPolygon con varios componentes o anillos (incluidos huecos) se visualizan con una capa GeoJSON en modo lectura. El JSON original oculto se mantiene al cambiar solo el nombre; una edición fiel no disponible no reemplaza silenciosamente la geometría.
- La geometría de la API sigue siendo validación definitiva. La página rechaza JSON malformado, tipos ajenos a Polygon/MultiPolygon y cargas mayores a 1 MiB antes de llamar al cliente; no reproduce geometrías ni excepciones técnicas dentro de los errores.
- Configuración del proceso Backoffice: `Mapbox__PublicAccessToken` (solo token público `pk.`) y opcional `Mapbox__StyleUrl` (por defecto `mapbox://styles/mapbox/streets-v12`). Sin token público válido, se explica la falta de mapa y no se permite guardar geometría dibujada. La copia desde catálogo local no se descarga ni importa automáticamente.
- Un token público se expone al navegador por diseño. Crear uno con permisos mínimos para lectura de estilos/recursos, restringir sus URLs a los orígenes necesarios (incluido el host/puerto local de desarrollo por separado) y usar otro token para cada ambiente; no colocar tokens secretos `sk.` en la configuración del Backoffice. Fuente: [tokens públicos y restricciones de URL](https://docs.mapbox.com/help/getting-started/access-tokens/) y [guía de administración de tokens](https://docs.mapbox.com/accounts/guides/tokens/).

### Franjas, versiones y consulta

En alta y edición, las horas se seleccionan mediante cuatro controles accesibles, agrupados bajo **Inicio** y **Fin**: hora 00–23 y minuto 00–59, siempre con dos dígitos y sin una selección inicial obligatoria inventada. Un horario nuevo se compone como `HH:mm:00.000000`; al editar, se conserva la precisión original si sus selecciones no cambian. Al modificar las selecciones de un horario, se compone con los minutos elegidos y segundos/fracciones en cero. Día, estado y selecciones se conservan ante errores. La interfaz indica finalización al día siguiente e igualdad inválida; mantiene inicio incluido/final excluido y remite la validación definitiva de solapamientos y domingo–lunes al backend. Cada alta/edición aceptada vuelve a leer el agregado para obtener su nueva versión/revisión.

Evidencia del ajuste de selects, según el informe de implementación: build Release de Backoffice sin advertencias ni errores, 4 pruebas enfocadas aprobadas, `node --check` y `git diff --check` correctos. La revisión Chrome de escritorio/móvil de ese corte usó HTML Razor generado por pruebas y API simulada; no se confunde con la validación manual posterior del usuario.

La versión se bindea como nullable, por lo que cero es válida y la ausencia se diferencia de cero. Al detectar una versión obsoleta, el formulario conserva lo que el usuario editó, bloquea la reejecución y ofrece recargar; no repite la mutación ni sustituye por una versión nueva. Los 409 de reglas de franjas se presentan como superposición o requisito de franja activa cuando la respuesta API existente los identifica; otros conflictos tienen mensaje conservador y opción de recarga.

En el mapa de detalle, seleccionar un punto completa longitud/latitud y requiere una acción POST explícita. El ApiClient consulta el contexto del operador, y la página presenta SinCobertura o todas las coincidencias y franjas activas, indicando cuando requiere selección de zona sin elegir una automáticamente. No calcula precio, promesa por fecha, cupos ni operador del envío.

### Evidencia del corte de interfaz y limitaciones históricas

Las limitaciones siguientes describen aquella ejecución. El cierre vigente incorpora la configuración posterior de Mapbox y la confirmación manual del usuario, sin reescribir los resultados históricos.

- `dotnet build src/NexoRuta.Backoffice/NexoRuta.Backoffice.csproj -c Release --no-restore`: aprobado, 0 advertencias y 0 errores.
- `dotnet test tests/NexoRuta.ApiClient.Tests/NexoRuta.ApiClient.Tests.csproj -c Release --no-restore --no-build --filter 'FullyQualifiedName~Zone|FullyQualifiedName~ZonasCobertura|FullyQualifiedName~MissingMapboxConfiguration|FullyQualifiedName~ZeroIsValidAggregateVersion|FullyQualifiedName~SlotConflictExplains|FullyQualifiedName~CoverageQuery|FullyQualifiedName~CoverageEndpoints|FullyQualifiedName~CoverageOpenApi'`: 16 aprobadas, 0 fallidas. Incluyen páginas con API doble y contratos del ApiClient/OpenAPI: autorización/navegación, catálogo vacío, alta, errores, versiones y conflictos, precisión de franjas y resultado de cobertura múltiple. Son respuestas simuladas, no integración con PostGIS ni con el API desplegado.
- `node --check src/NexoRuta.Backoffice/wwwroot/js/zonas-cobertura.js`: aprobado.
- `git diff --check` en raíz, `frontend` y `backend`: aprobado, sin errores de whitespace.
- En la evidencia previa no había un navegador/token configurado. En la comprobación del 2026-10-19 se constató Edge/Chrome/Firefox instalados y las páginas/activos del Backoffice responden en Compose, pero no se simuló ni se declara validado el mapa visual: el contenedor no recibe un token Mapbox no vacío. La salida HTML de los tests no sustituye esa comprobación.
- Las pruebas reales PostGIS/HTTP enfocadas ya se ejecutaron con Testcontainers efímero (ver abajo); no se importó el catálogo departamental. El PostgreSQL persistente local se actualizó de imagen y migración sin reset ni borrado de datos.

Referencias Mapbox consultadas para la versión y la herramienta: [Getting Started GL JS](https://docs.mapbox.com/mapbox-gl-js/guides/get-started/), [Add drawing tools](https://docs.mapbox.com/mapbox-gl-js/example/mapbox-gl-draw/) y [Mapbox Draw API](https://github.com/mapbox/mapbox-gl-draw/blob/main/docs/API.md).

## Puesta en funcionamiento local y verificación — PostgreSQL/PostGIS (corte anterior, 2026-10-19)

### Docker, Testcontainers y precisión de respuestas

- La única comprobación de Docker de esta pasada respondió: Docker Desktop `4.40.0`, Engine `28.0.4`, Linux/amd64 sobre WSL2 y contexto `desktop-linux`. No se repitió el diagnóstico anterior ni se cambió `DOCKER_HOST` o el contexto.
- La imagen efímera de pruebas fue `postgis/postgis:18-3.6-alpine`; las bases se crearon dentro del contenedor Testcontainers y se eliminaron con él. No se apuntó a PostgreSQL Compose durante estas pruebas.
- Comando: `dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~ZonaCoberturaPostgisTests|FullyQualifiedName~ZonasCoberturaHttpTests' --logger 'console;verbosity=minimal'`. Resultado final: **9 aprobadas, 0 fallidas, 0 omitidas** (8 de PostGIS/persistencia/RLS y 1 recorrido HTTP enfocado). Se ejercitaron migración desde esquema vacío, PostGIS/SRID/geometría, microsegundos e historial, inmutabilidad, franjas nocturnas y domingo–lunes, activación, concurrencia/rollback, aislamiento con rol API restringido y coincidencias espaciales incluidas bordes y superposiciones.
- La primera ejecución reveló que la respuesta HTTP de `TimeOnly` agregaba un séptimo decimal (`.1234560`) a valores `time(6)`. La respuesta de franja ahora usa un conversor limitado al DTO HTTP que conserva el formato contractual de seis dígitos (`.123456`); la segunda ejecución del grupo enfocado pasó.
- La misma ejecución inicial mostró un 200 al leer una zona ajena porque ese test estaba ejecutando el API con el usuario administrador de la base, que evade RLS. Se corrigió el fixture HTTP para sembrar con el contexto de prueba existente y ejecutar el API con un login restringido, `NOSUPERUSER NOBYPASSRLS` con membresía `nexoruta_api`; el caso ahora confirma 404 y el grupo terminó 9/9. No fue un cambio para relajar ni para omitir RLS.
- `dotnet build src/NexoRuta.Api/NexoRuta.Api.csproj -c Release --no-restore`: aprobado, 0 advertencias/errores. `dotnet build ../frontend/src/NexoRuta.Backoffice/NexoRuta.Backoffice.csproj -c Release --no-restore`: aprobado, 0 advertencias/errores. `dotnet ef migrations has-pending-model-changes --project src/NexoRuta.Infrastructure/NexoRuta.Infrastructure.csproj --startup-project src/NexoRuta.Api/NexoRuta.Api.csproj --context NexoRutaDbContext`: “No changes have been made to the model since the last migration.”

### PostgreSQL/PostGIS persistente del Compose local

- Destino comprobado: proyecto Compose `nexoruta`, servicio `postgres`, base `nexoruta`. Antes del cambio, el servidor era PostgreSQL `18.6` (`server_version_num=180006`), `PGDATA=/var/lib/postgresql/18/docker`, volumen `nexoruta_postgres_data` montado en `/var/lib/postgresql`; la imagen de desarrollo era `postgres:18-alpine`.
- Se verificó la imagen `postgis/postgis:18-3.6-alpine` disponible para Linux/amd64, con la misma versión mayor PostgreSQL 18 y la misma ruta/semántica de volumen para PG18. Se recreó exclusivamente `postgres` con el mismo volumen nombrado, sin `down -v`, sin reset, sin respaldo, y sin tocar volúmenes de RabbitMQ/Kafka/MongoDB/Data Protection. El servicio quedó saludable y la base conservó los datos previos: 1 operador, 1 comercio y 3 usuarios; no se modificaron sus cuentas/contraseñas.
- Se comprobó que la única migración pendiente era `20261019010048_AddCoverageZonesAndDeliverySlots`. Se ejecutó únicamente `docker compose -p nexoruta -f compose.yaml -f compose.dev.yaml --profile rls-admin run --rm --no-deps --build rls-migrator`; el historial quedó en esa migración. El proceso API conserva `DatabaseInitialization:ApplyMigrations=false` y `DatabaseInitialization:SeedData=false`. No se ejecutaron `local-bootstrap`, seeders generales ni provisionadores de roles.
- Resultado de esquema: PostGIS `3.6.4`; cuatro tablas nuevas (`DepartamentosReferencia`, `ZonasCobertura`, `FranjasEntrega`, `ZonasCoberturaRevisiones`) con `ENABLE` y `FORCE ROW LEVEL SECURITY`, propietarias `nexoruta_schema_owner`; `ZonasCobertura` tiene índice GiST y clave única compuesta; la tabla técnica `spatial_ref_sys` no concede SELECT a `nexoruta_api`. El proceso API usa `nexoruta_api_runtime`, miembro de `nexoruta_api`; los logins runtime API/worker/bootstrap verificados mantienen `SUPERUSER=false` y `BYPASSRLS=false`. Las tablas de zonas, franjas e historial quedaron vacías: no se crearon registros de demostración en la base persistente.
- Se reconstruyeron/actualizaron solo `postgres`, `api` y `backoffice` (además del contenedor temporal migrador). API `GET /health/ready` y Backoffice `GET /health/ready` respondieron 200; `/ingresar` respondió 200, `/zonas` redirigió a autenticación con 302, sus activos `css/zonas-cobertura.css` y `js/zonas-cobertura.js` respondieron 200, y el listado API anónimo devolvió 401. Esto acredita disponibilidad de rutas/activos, no un login ni una interacción visual autenticada.
- En aquella comprobación el `compose.yaml` ya pasaba `Mapbox__PublicAccessToken` y `Mapbox__StyleUrl` a `backoffice`, pero el token estaba vacío. La inspección posterior de la tarea ubicó la configuración pública efectiva, descrita en la sección vigente al final.
- En aquel corte no se modificó `levantar-entorno-dev.bat`; esta tarea lo actualizó para invocar el importador después de las migraciones. El script conserva el bootstrap/seeders existente separado del comando de importación. Nunca se debe colocar un token secreto `sk.` en Backoffice.

### Estado previo al catálogo de Uruguay (evidencia histórica)

En esa comprobación anterior el backend real con PostGIS ya estaba migrado, pero el catálogo estaba vacío y no se había configurado Mapbox; por ello no se completó el recorrido visual ni se escribieron zonas/franjas locales de prueba. La importación y la configuración actual se verifican en la sección siguiente. La evidencia de esa ejecución no se considera una comprobación del mapa visual actual.

## Catálogo departamental y ámbito Uruguay (estado vigente, 2026-10-19)

### Fuente fijada e importador

El artefacto incluido es `backend/src/ZonasCobertura/Data/departamentos-uruguay.geojson` (el directorio `src/NexoRuta.Infrastructure/ZonasCobertura/Data` mencionado en algunos antecedentes no existe en este checkout). `NexoRuta.Infrastructure.csproj` lo incorpora como recurso embebido; la carga no depende del directorio de trabajo. La URL, checksum, mapeo, CRS y atribución observados están en el [README del catálogo](../../src/ZonasCobertura/Data/README.md).

Validación observada: FeatureCollection, 21 features agrupadas en 19 nombres `DEPTO` únicos, sin departamentos duplicados; 405.249 posiciones 2D finitas en orden longitud/latitud y compatibles con WGS 84/Uruguay. La descarga solicitó `outSR=4326`; no se ejecutó reproyección adicional. El servicio reporta WKID 5381 en el campo de referencia espacial, 5382 en el extent, y el contexto de la descarga indica EPSG:5382; el README registra la discrepancia. SHA-256 real: `6853cf948843aeaaefe277c09ecbfc01fe824231ede4e824aa03a045c6864219`.

Los nombres se asignan explícitamente a los 19 códigos ISO 3166-2 `UY-*`; no se usan `OBJECTID` ni orden. Se conservan islas, componentes y huecos. La agrupación directa de ARTIGAS producía autointersección topológica por piezas superpuestas: cuando el agregado resulta inválido, se ejecuta una unión topológica exacta de esas piezas, sin `ST_MakeValid`, recorte, simplificación ni desplazamiento. El servicio consultado atribuye la capa al IGM pero no declara licencia ni edición; no se infieren y queda pendiente la revisión de derechos de reutilización.

El importador reusa `DepartamentoReferencia`, valida el archivo completo antes de abrir una transacción e inserta los 19 registros atómicamente. `VersionFuente` es `sha256:<checksum>`. La misma versión es no-op; un checksum diferente se rechaza salvo el comando explícito `department-catalog-import --allow-source-update`, que añade una versión y conserva la anterior. `department-catalog-import` es un proceso de una sola tarea que termina sin Kestrel ni bootstrap/seeder. `levantar-entorno-dev.bat` lo invoca después de migraciones mediante `rls-migrator`; API y workers permanecen de solo lectura.

### Referencia territorial y validación

La geometría territorial operativa deriva del GeoJSON oficial embebido, no del catálogo importado. `GET /api/configuracion/zonas/departamentos/territorio` entrega contorno, checksum y límites; `GET /api/configuracion/zonas/departamentos/{codigo}` entrega geometría/límites por código. `GET /api/configuracion/zonas/departamentos` conserva la lista de filas persistidas y puede responder vacía sin bloquear mapa, dibujo o creación de geometría propia. ApiClient y OpenAPI conservan sus contratos.

La infraestructura carga el recurso embebido una vez por proceso mediante `Lazy`; usa sus 19 geometrías oficiales para el contorno/cámara y la colección sin disolver para validación. PostGIS ejecuta `ST_Covers(@territory, @candidate)` sobre la fuente canónica; la extensión/cámara del mapa nunca se usa como validación. Esto evita que una tabla de referencias ausente habilite un mundo o rectángulo alternativo.

Se valida toda geometría antes de la restricción territorial en alta dibujada o por departamento, cambio geométrico, activación y consulta del punto. `ST_Covers` incluye los bordes. Punto o zona fuera se rechazan con error controlado; si el recurso canónico no pudiera cargarse, no se sustituye por el mundo o un rectángulo. Se conservan coincidencias múltiples, RLS, revisiones y versiones. No se recortan ni cambian zonas ya existentes.

### Backoffice, Mapbox y Compose local

Crear zona genera y sirve desde el mismo GeoJSON un asset estático del Backoffice en desarrollo, publish y Docker. Mapbox inicia en paralelo, y no espera consultas territoriales; dibujar geometría propia sigue disponible con la capa departamental fallida. La selección/copia lee el asset oficial y envía el código estable, no una FK/ID de base. Los otros mapas conservan las consultas de territorio del API. Fallos de Mapbox continúan visibles y con reintento.

El Backoffice efectivo recibe `Mapbox__PublicAccessToken` de tipo `pk.`; no se imprime ni se almacena aquí. Se conserva la URL de estilo por defecto cuando no se configura una propia. El token público sigue sujeto a restricciones de URL/origen recomendadas en la sección Mapbox.

Destino real comprobado: proyecto Compose `nexoruta`, servicio `postgres`, base `nexoruta`, PostgreSQL 18.6/PostGIS 3.6.4, `PGDATA=/var/lib/postgresql/18/docker`; la migración ya estaba aplicada. El API mantiene `ApplyMigrations=false` y `SeedData=false`. Se reconstruyeron API y el contenedor temporal `rls-migrator`, y se recrearon solamente API y Backoffice; no se alteraron Compose, credenciales, cuentas, roles ni volúmenes.

La carga local dejó 19 filas, 19 códigos únicos, una versión SHA-256 y cero geometrías inválidas/vacías/SRID distinto de 4326. La segunda ejecución informó `alreadyImported=True`. `DepartamentosReferencia` conserva ENABLE/FORCE RLS, owner `nexoruta_schema_owner`, SELECT para `nexoruta_api` y ningún INSERT para ese rol.

Antes y después de la importación existían dos zonas. Una zona válida preexistente, `san jose casi` (`01a153ce-51d0-720a-b645-832bfe3569ab`), no queda cubierta por la referencia oficial actual: bounds aprox. -57.1494,-34.8718 a -56.0699,-34.1413; `ST_Covers` resultó falso y el área de diferencia fue 0.0191814038 unidades cuadradas de grados. Está Inactiva, versión 0, revisión 1. Se dejaron ambas zonas sin cambios; la validación territorial rechazará activarla o reemplazar su geometría hasta que el operador la revise. No se crearon datos de demostración.

### Pruebas y límites de evidencia automatizada

- Prueba enfocada `dotnet test backend/tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~UruguayCatalogAndTerritoryTests|FullyQualifiedName~ZonasCoberturaHttpTests.OperatorManagesZonesAndSlotsAndQueriesAllBoundaryMatchesThroughHttp'`: 5 aprobadas. Testcontainers usó almacenamiento PostgreSQL/PostGIS efímero, separado del Compose local. Comprueba 19 códigos, importación repetible sin cambios de fecha, archivo inválido sin escritura parcial, actualización de fuente solo explícita, MultiPolygon/SRID, permisos RLS restringidos, geometría cruzando el límite rechazada sin revisión parcial, punto interior/borde/exterior y coincidencias superpuestas. El API devuelve el MultiPolygon de San José (`UY-SJ`) y permite su consulta autenticada.
- La prueba HTTP restringida se repitió después de añadir la lectura de San José (`UY-SJ`): 1 aprobada. Contratos OpenAPI (2 pruebas), pruebas Backoffice enfocadas (7 pruebas), builds Release de API/Backoffice y `node --check` también pasaron; las pruebas Backoffice usan dobles de API.
- En el Compose local, API y Backoffice respondieron 200 en health, activos CSS/JS 200, página protegida redirigió al login sin sesión y el endpoint API de territorio anónimo respondió 401. El JS servido tiene bounds dinámicos y no crea copias horizontales.
- No se completó selección/dibujo de San José ni recorrido visual de Mapbox en navegador, escritorio o móvil: esta sesión no dispone de harness Playwright/Puppeteer ni herramienta de navegador. Aunque el contenedor tiene un token público `pk.`, no se declara que Mapbox haya renderizado ni que su interacción esté probada; tampoco hubo login local.

## Cierre y validación manual — 2026-10-19

**CU-02 queda cerrado funcionalmente en el alcance acordado.** Después de la importación departamental y la restricción a Uruguay, el usuario confirmó que quedó bien; previamente también confirmó el funcionamiento del Backoffice de zonas y franjas. Se registra como aceptación manual comunicada por el usuario, no como una ejecución de navegador automatizada por el agente.

La sesión de implementación del catálogo no contó con una herramienta automatizable de navegador. Esa limitación histórica se conserva: no se inventan capturas, pasos individuales, resultados por dispositivo ni una ejecución automatizada que no existió. La confirmación manual posterior supera el pendiente de aceptación visual del usuario; las pruebas técnicas enfocadas mantienen sus resultados originales.

El alcance cerrado incluye configuración de zonas y franjas por operador, geometrías dibujadas o copiadas del catálogo, activación/desactivación, consulta de cobertura, concurrencia, revisiones inmutables, aislamiento RLS y ámbito Uruguay. Las tarifas, la geocodificación de direcciones, la promesa concreta del envío y la planificación consumidora corresponden a sus respectivos casos de uso; no se agregan como pendientes funcionales de CU-02.

Se conserva la confirmación pendiente de licencia y edición del archivo de referencia en su documentación de procedencia. No se declara resuelta ni se altera el dataset para este cierre documental. La zona local preexistente fuera del ámbito informado también se conserva sin modificación; su revisión corresponde al operador, sin corrección automática ni pérdida de datos.

Este cierre documental reutiliza la evidencia registrada y la aceptación del usuario. No implica una nueva ejecución de pruebas, revisión del código fuente, operación sobre la base, cambio de servicios, commit ni push.

## Actualización — catálogo opcional y fuente territorial embebida (2026-10-22)

Esta actualización reemplaza la conclusión anterior de que cargar `DepartamentoReferencia` era condición para usar Crear zona. La importación sigue siendo un mecanismo idempotente para poblar el catálogo persistido, pero no es un requisito de ejecución de la pantalla ni de la validación de geometría.

- Fuente única: `src/ZonasCobertura/Data/departamentos-uruguay.geojson`, incluido como recurso embebido de backend y generado como asset estático de Backoffice desde el mismo archivo. El contenido comprobado tiene SHA-256 `6853cf948843aeaaefe277c09ecbfc01fe824231ede4e824aa03a045c6864219`; sus 21 componentes corresponden a 19 departamentos.
- El API deriva territorio y departamento por código desde el recurso embebido. El repositorio usa `ST_Covers` contra las geometrías oficiales agrupadas, mantiene las validaciones GeoJSON/PostGIS y rechaza geometrías fuera de Uruguay. La tabla de referencias puede continuar vacía.
- El código/version de procedencia de una copia departamental ya no tiene FK a la tabla. La migración `20261023014518_AllowEmbeddedDepartmentZoneProvenance` elimina esa FK y su índice; fue comprobada con la cadena de migraciones en PostgreSQL efímero. No se aplicó a la base persistente local durante esta corrección. Dibujo propio no depende de esa migración porque no almacena código de departamento.
- Evidencia efímera enfocada: `EmptyReferenceTableStillServesOfficialTerritoryAndCreatesCustomZone`, `OperatorManagesZonesAndSlotsAndQueriesAllBoundaryMatchesThroughHttp` y `OperatorConfiguresSimulatesPublishesAndCopiesTarifarioThroughHttp` — 3/3. El primer caso cubre lista vacía, fuente territorial, zona dibujada y recuperada, franja, activación, asociación de tarifa al mismo ID, copia por código sin filas de referencia y rechazo fuera del territorio. El test HTTP de gestión de zonas también conserva vacío el catálogo durante sus comprobaciones de franjas/cobertura.
- Evidencia de runtime: Backoffice Docker sirvió el GeoJSON estático con el mismo checksum y el navegador, tras ingreso sintético normal, cargó Mapbox GL/Draw y sus recursos reales, mostró el contorno y 19 opciones; el dibujo produjo un Polygon y la selección resaltó UY-MO. No se envió el formulario ni se modificó la base persistente.
- No se importó el catálogo, no se migró ni se escribió en la base local. La revisión nativa sigue pendiente y sin aprobación; el estado de RDD se mantuvo.
