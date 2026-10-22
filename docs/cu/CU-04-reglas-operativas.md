# CU-04 — Reglas operativas del operador

> **Estado actual:** Fases 2–4 implementadas. La migración de CU-04 y una corrección del trigger fueron aplicadas al Compose local `nexoruta`; API y Backoffice recorrieron por HTTP el flujo de borrador, conflicto, publicación e historial. La revisión visual real en escritorio y móvil sigue pendiente porque esta sesión no dispuso de automatización de navegador. CU-04 no se declara cerrado hasta esa comprobación manual.

## 1. Alcance y trazabilidad

### Requisitos textuales

| Fuente | Lo que establece | Límite de lo que establece |
|---|---|---|
| [Laboratorio, §3.1–3.3, PDF p. 3 (impresa p. 2)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | Los intentos fallidos carecen hoy de un motivo/evidencia confiable; las devoluciones carecen de trazabilidad; el operador configura su propia operación y reglas; el operador es el tenant y un comercio puede trabajar con más de uno. | No define valores iniciales, triggers automáticos de devolución, ni modelo de publicación. |
| [Laboratorio, §4, PDF p. 4 (impresa p. 3)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | Backoffice Razor Pages administra reglas; el repartidor usa una aplicación móvil que registra entregas e intentos y debe funcionar sin conectividad. | No asigna a Commerce o Plataforma la administración de reglas. |
| [Laboratorio, §5.1, requisito 4, PDF p. 4 (impresa p. 3)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | El operador configura: cantidad máxima de intentos, plazo entre intentos, prueba exigida, catálogo de motivos válidos de no entrega, política de devolución y plazos por modalidad. | Especifica explícitamente la modalidad solo para los plazos; no afirma que cada regla varíe por modalidad. No da unidades ni valores. |
| [Laboratorio, §5.2, requisitos 11–12, PDF p. 5 (impresa p. 4)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | El envío usa una máquina de estados explícita y verificable; estados mínimos: Admitido, En depósito, Asignado a ruta, En tránsito, Entregado, No entregado, Reprogramado, En devolución, Devuelto y Extraviado. Los eventos deben conservar tiempo, origen, responsable y ubicación cuando corresponda. | No pide que CU-04 implemente esas transiciones ni un registro de intentos. |
| [Laboratorio, §5.4, requisitos 20–22, PDF p. 5 (impresa p. 4)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | CU-20 registra firma en pantalla, fotografía, nombre y documento del receptor según configuración, además de posición y hora del dispositivo; CU-21 registra el intento fallido con motivo del catálogo y evidencia; CU-22 gestiona devolución y rendición al regresar al depósito. | No determina si las pruebas seleccionadas son alternativas o requisitos acumulativos, ni cuándo una devolución se inicia automáticamente. |
| [Laboratorio, §5.5, requisito 26, PDF p. 5 (impresa p. 4)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | La solicitud de reprogramación del destinatario está sujeta a las reglas del operador. | No define número de reprogramaciones, nuevas fechas permitidas ni un algoritmo de calendario. |
| [Laboratorio, §§6.4–6.6, PDF pp. 6–7 (impresas pp. 5–6)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | Se requiere concurrencia optimista demostrable; aislamiento multitenant efectivo con dos operadores; tarifas y reglas editables en ejecución y versionadas, preservando el tratamiento anterior de envíos. | No prescribe tablas, bloqueo ni interfaz de publicación. |
| [Laboratorio, §6.11 y §6.15, PDF p. 8 (impresa p. 7)](../../../docs/alcance/Laboratorio__NET_2026.pdf) | La app móvil captura evidencia, opera offline con almacenamiento local cifrado y sincroniza con una política de conflictos documentada; se requieren pruebas unitarias, integración crítica y aislamiento. | Esos flujos pertenecen a M04 y no se construyen en CU-04. |
| [XLSM, `CU obligatorios!A7:I7`](../../../docs/alcance/Calcs/Casos%20de%20Uso%20-%20Responsables.xlsm) | CU-04 “Reglas operativas”: “Configurar intentos, intervalos, prueba exigida, motivos, devoluciones y plazos por modalidad.” M01, §5.1, Backoffice, actor Operador/administrador, obligatorio. | La fila no asigna responsable individual. `Módulos!A4:F4` asigna M01 al área de Administración y configuración y nombra a José Garcia como responsable del módulo, no como asignación personal de CU-04. |

La ruta real de la planilla es `docs/alcance/Calcs/Casos de Uso - Responsables.xlsm` (mayúscula `C` en `Calcs`). Se inspeccionó el contenido del XLSM sin ejecutar macros.

### Dependencias registradas en la planilla

| Registro | Rango XLSM | Relación y contrato indicado |
|---|---|---|
| D03 | `Dependencias!A6:I6` | CU-11 depende de CU-04: estados de intento, devolución y plazos usan reglas del operador; el contrato propuesto es la versión de reglas asociada al envío. El riesgo es aplicar transiciones distintas entre operadores. |
| D10 | `Dependencias!A13:I13` | CU-20 depende de CU-04: la prueba requerida varía por operador; propone una política de evidencia versionada. |
| D11 | `Dependencias!A14:I14` | CU-21 depende de CU-04: los motivos válidos proceden del catálogo del operador, versionado. |
| D16 | `Dependencias!A19:I19` | CU-26 depende de CU-04: la reprogramación respeta intentos y plazos; propone un validador de reprogramación. |
| D13 | `Dependencias!A16:I16` | CU-22 depende de CU-12 para eventos trazables de devolución; no declara una arista directa CU-22→CU-04. CU-04 sí define la política que CU-22 deberá interpretar. |

Las filas de consumidor están en `CU obligatorios!A14:I14` (CU-11), `A23:I23` (CU-20), `A24:I24` (CU-21), `A25:I25` (CU-22) y `A29:I29` (CU-26).

### No funcionales aplicables

- **NFR-03 / §6.3:** autorización por actor/tenant y secretos fuera del código. Referencia: `No funcionales!A6:F6`.
- **NFR-04 / §6.4:** concurrencia optimista demostrada. `No funcionales!A7:F7`.
- **NFR-05 / §6.5:** aislamiento de lectura/escritura y demostración con dos operadores. `No funcionales!A8:F8`.
- **NFR-06 / §6.6:** reglas editables en caliente y versionadas; envíos anteriores conservan tratamiento. `No funcionales!A9:F9`.
- **NFR-11 / §6.11:** operación móvil offline cifrada y sincronización con resolución de conflictos; CU-04 entrega la versión, M04 la consume. `No funcionales!A14:F14`.
- **NFR-15 / §6.15:** pruebas de dominio, integración crítica y aislamiento. `No funcionales!A18:F18`.
- **NFR-01/02 / §§6.1–6.2:** si se implementa, conservar separación de dominio y Backoffice Razor Pages; no justifican una aplicación o framework adicional. `No funcionales!A4:F5`.

NFR-08 sobre outbox/reintentos no convierte los intentos de entrega en reintentos técnicos. CU-04 no requiere un worker ni una tarea programada para publicar reglas; los eventos que registren cambios de envío son responsabilidad de CU-11/CU-12 y seguirán su contrato propio.

### Decisiones del equipo, no requisitos textuales

La publicación explícita, un borrador único por operador, la estructura de tablas, el alcance global por operador de las reglas no asociadas a modalidad, la semántica AND de pruebas seleccionadas, el punto de partida del plazo, la política al agotar intentos y los límites técnicos de validación son decisiones del equipo, no requisitos textuales del laboratorio.

## 2. Reutilización real y brechas actuales

| Área | Existe y se reutiliza | Falta para CU-04 o pertenece a otro CU |
|---|---|---|
| Envío/estados | [`Envio`](../../src/NexoRuta.Domain/Envios/Envio.cs) conserva `OperadorId`, `ComercioId` y `Estado`; [`EstadoEnvio`](../../src/NexoRuta.Domain/Envios/EstadoEnvio.cs) hoy solo contiene `Admitido`. [`CrearEnvioUseCase`](../../src/NexoRuta.Application/Envios/UseCases/CrearEnvioUseCase.cs) resuelve el Comercio desde el contexto persistido y valida el Operador seleccionado por solicitud. | `Envio` todavía no guarda modalidad, fecha de admisión, publicación de reglas, plazo absoluto ni intentos. Las transiciones declarativas y el historial inmutable exigidos pertenecen principalmente a CU-11/CU-12. |
| Alta y mensajería | [`EfEnviosRepository.GuardarAsync`](../../src/NexoRuta.Infrastructure/Envios/Repositories/EfEnviosRepository.cs) persiste el envío con `GenerarSeguimiento` hacia RabbitMQ y `EnvioCreado` hacia Kafka en la misma transacción. Los tipos están cerrados en [`OutboxMessageTypes`](../../src/NexoRuta.Infrastructure/Outbox/OutboxMessage.cs). | Ninguno de esos mensajes es un intento fallido. No existe en este flujo un registro de visita física, evidencia de no entrega ni contador de intentos. |
| Modalidades | [`ModalidadTarifaria`](../../src/NexoRuta.Domain/Tarifas/ContratosTarifarios.cs) ya define `Estandar` y `Urgente`. | CU-04 reutilizará esos dos valores; no debe crear un segundo catálogo ni redefinir qué significan “Estándar” y “Urgente”. El plazo de cada uno sí es configuración propia de reglas. |
| Versionado tarifario | [`VersionTarifario`](../../src/NexoRuta.Domain/Tarifas/VersionTarifario.cs) distingue `VersionConcurrencia` de `NumeroVersionPublicada`, copia publicaciones a un borrador y no publica un tarifario incompleto. [`EfTarifariosRepository`](../../src/NexoRuta.Infrastructure/Tarifas/EfTarifariosRepository.cs) serializa crear/copiar, editar y publicar con el bloqueo común de la raíz del operador. La migración [`AddTarifarioOperador`](../../src/NexoRuta.Infrastructure/Persistence/Migrations/20261019152350_AddTarifarioOperador.cs) protege publicaciones y sus hijos persistentemente. | Reutilizar el patrón, no la entidad, tablas ni hijos tarifarios. El historial de reglas y el de tarifas son agregados independientes. |
| Identidad y permiso | [`IUsuarioActual`](../../src/NexoRuta.Application/Administracion/Interfaces/IUsuarioActual.cs), [`ContextoUsuario`](../../src/NexoRuta.Application/Administracion/Context/ContextoUsuario.cs), `CatalogoPermisos.ConfiguracionGestionar` y [`RequerirOperadorCoberturaUseCase`](../../src/NexoRuta.Application/ZonasCobertura/UseCases/RequerirOperadorCoberturaUseCase.cs) dan el patrón vigente: acceso persistido activo, ámbito Operador y permiso dinámico. | Reutilizar el patrón/guard existente. Un rol o el permiso aislado no bastan: Comercio también puede tener `configuracion.gestionar`; Plataforma usa un contexto separado. |
| RLS y transacciones | [`RlsTransactionMiddleware`](../../src/NexoRuta.Api/Core/Security/RlsTransactionMiddleware.cs) revalida el acceso persistido, abre la transacción y aplica el contexto RLS; [`RlsTransaction`](../../src/NexoRuta.Infrastructure/Persistence/RlsTransaction.cs) permite unirse a esa transacción. CU-02/CU-03 ya usan RLS, claves compuestas y propietario separado. | Las nuevas tablas deberán usar el mismo `NexoRutaDbContext`/transacción y RLS por `OperadorId`, no un tenant paralelo o una conexión privilegiada. |
| Backoffice | El Backoffice actual tiene Razor Pages y una feature Tarifas bajo `frontend/src/NexoRuta.Backoffice/Features/Tarifas`; CU-03 ofrece el patrón de estados, borrador, confirmación, historial y conflicto. | Crear la feature de reglas en una fase posterior; esta entrega no crea navegación, cliente HTTP ni páginas. |

**Conclusión:** la brecha de CU-04 es un agregado versionado de configuración. No hay aún estados de envío más allá de `Admitido`, contador/registro de visitas, catálogo de no entrega ni captura de prueba en el backend observado. No se describen como existentes.

## 3. Alcance y matriz de reglas propuesta

Solo los plazos varían por modalidad. La frase del requisito agrupa intentos, intervalo, evidencia, motivos y devolución como reglas **del operador**, y agrega los plazos **por modalidad**. No se propone duplicar cinco configuraciones por Estándar/Urgente. El alcance es un conjunto de reglas por operador con dos duraciones explícitas.

| Regla | Significado y ámbito | Tipo/unidad y validación propuesta | Ejemplo | Consumidor futuro | Si falta configuración |
|---|---|---|---|---|---|
| Máximo de intentos | Máximo de visitas físicas de entrega por envío, Operador. Cada visita queda registrada por el repartidor; una sincronización repetida no cuenta otra vez. Una visita exitosa también es un intento y termina el flujo. | `int MaxIntentosEntrega`; `>= 1`. No se cuenta asignar una ruta ni reprogramar una visita. | Máximo 3: se permiten hasta tres visitas; tras la tercera fallida no se habilita una cuarta. | CU-21 registra; CU-11 aplica estado y límite; CU-26 valida solicitudes posteriores. | No publicar una configuración incompleta. Sin publicación vigente, rechazar de forma controlada la acción que necesita esta regla; no asumir un máximo por defecto. |
| Intervalo | Espera mínima desde el intento fallido anterior hasta el siguiente intento, sin importar si el siguiente termina bien o mal; ámbito Operador. No se mide entre mensajes ni entre solicitudes de reprogramación. | `int IntervaloEntreIntentosMinutos`; `1..525.600`. El reloj, eventos offline y orden de sincronización pertenecen a CU-24 y no se implementan en esta fase. | 24 h = 1.440 min: tras una falla ocurrida el lunes 10:00, el próximo intento no puede iniciarse antes del martes 10:00; su resultado no cambia que sea el siguiente intento. | CU-11 impone el mínimo; CU-21 registra la visita; CU-26 no reinicia ni desplaza el intervalo por pedir una reprogramación. | No usar intervalo cero ni reintento inmediato implícito. Sin regla vigente, operación bloqueada con error de configuración controlado. |
| Prueba de entrega | Datos/evidencias exigidos para registrar una entrega exitosa, Operador. Cada prueba exigida es obligatoria: semántica AND. | Conjunto no vacío de enum cerrado: `FirmaEnPantalla`, `Fotografia`, `NombreReceptor`, `DocumentoReceptor`; sin duplicados. Posición y hora de captura del dispositivo son obligatorias por §5.4 #20, independientes de la selección. | Firma + nombre: ambos deben estar presentes; no alcanza con uno. | CU-20 captura y valida en servidor contra la publicación asociada al envío; M04 guarda offline cifrado. | No aceptar transición a `Entregado` sin una política publicada y todos sus elementos requeridos. |
| Motivo de no entrega | Catálogo seleccionado al registrar una visita fallida, Operador. El motivo clasifica el resultado; no es el estado del envío ni una autorización. | Por versión: `Codigo varchar(40)`, `Descripcion varchar(120)`, `Disponible bool`. El operador propone el código explícitamente al crear el motivo; no se deriva de la descripción. Formato ASCII `A-Z`, seguido de `A-Z`, `0-9` o `_`; único e inmutable. Se conserva en copias y solo se puede retirar disponibilidad, nunca borrar/reutilizar. Descripción obligatoria y editable. | `DOMICILIO_CERRADO` — “No había quien recibiera el envío”. El código, no el texto, se envía y valida. | CU-21 ofrece las descripciones de la versión del envío y persiste el código; CU-29 podrá agrupar por código. | No aceptar intentos fallidos sin un motivo válido. El catálogo publicado debe contener al menos un motivo disponible. |
| Política de devolución | Define la acción al agotarse el máximo de intentos fallidos, Operador. No decide por texto de motivo ni realiza la devolución física. | Enum cerrado `DecisionOperativa` o `IniciarAutomaticamente`; una sola opción obligatoria por publicación. La automaticidad solo se ejecuta al aceptar la última falla, en la transacción de CU-11/CU-21. | Máximo 3, política `DecisionOperativa`: tras la tercera falla queda `NoEntregado`, sin otro intento, y un responsable decide. Política `IniciarAutomaticamente`: esa transición inicia `EnDevolucion`. | CU-11 gobierna estado; CU-22 ejecuta devolución y rendición. | No inferir una devolución de un motivo o de un máximo ausente. La política es obligatoria para publicar. |
| Plazos comprometidos | Duración máxima desde que el envío queda admitido, con valor distinto para Estándar y Urgente, Operador + modalidad. | Dos `int` de minutos positivos (`PlazoEstandarMinutos`, `PlazoUrgenteMinutos`). Cómputo de tiempo transcurrido, no hábil; guardar instante límite en UTC y mostrarlo en `America/Montevideo`. La suma debe estar dentro del rango representable. | Admisión 19/10 15:00; Urgente 1.440 min → límite 20/10 15:00. Estándar 4.320 min → 22/10 15:00. Son ejemplos, no valores por defecto. | CU-07 fija modalidad y registra compromiso al admitir; CU-11 y CU-26 consultan versión/límite; CU-13/CU-14 validan compatibilidad de ruta; CU-29 puede detectar riesgo. | Requerir ambos valores al publicar. Si no hay versión vigente, no admitir una promesa que dependa de plazo; no publicar una fecha estimada inventada. |

Los nombres y topes de las opciones son una propuesta técnica, no vocabulario impuesto por el laboratorio. Para esta fase `MaxIntentosEntrega` admite 1–100; intervalo y plazos admiten 1–525.600 minutos (un año). Son límites técnicos explícitos, no defaults ni valores recomendados de operación. Los códigos admiten máximo 40 caracteres y las descripciones 120. Se evita aceptar fórmulas, permisos o reglas arbitrarias como texto. El campo de descripción del motivo es presentación; los consumidores usan su código y la versión asociada.

### Precisiones de comportamiento

1. **Intento vs. reprogramación.** Un intento es una visita real del repartidor con resultado registrado. CU-21 registra el fallo; CU-11 contabiliza una sola vez el evento aceptado. El intervalo mínimo desde un intento fallido se aplica hasta el siguiente intento, tanto si este termina exitoso como si falla. Reprogramar mueve una oportunidad futura, no crea una visita, no incrementa el contador y no reinicia el intervalo. Una reentrega técnica del mismo evento es idempotente y no cuenta como otro intento; la sincronización offline corresponde a CU-24.
2. **Agotar el máximo.** El máximo limita visitas totales. Tras `MaxIntentosEntrega` resultados fallidos no se permite una visita adicional. Éxito en cualquier visita lleva a `Entregado`; una falla final deja `NoEntregado` y aplica exactamente la política de devolución publicada. No se decrementa ni reinicia el contador al cambiar de ruta.
3. **Pruebas.** Las fuentes enumeran firma, fotografía, nombre y documento, pero no indican alternativas. Decisión adoptada: la selección es acumulativa (AND), al menos un tipo. `FirmaEnPantalla`/`Fotografia` representan artefactos; nombre/documento son campos estructurados. Ubicación y hora capturadas por el dispositivo se registran siempre para la prueba de entrega según §5.4 #20. CU-20 valida en el servidor; una pantalla o app offline no puede declarar por sí sola que una prueba quedó aceptada.
4. **Motivo vs. estado.** Por ejemplo, `DOMICILIO_CERRADO` es un motivo; `NoEntregado` es estado. El motivo no autoriza por sí mismo una devolución o reprogramación. Enviar texto libre nunca sustituye a un código vigente.
5. **Histórico de motivos.** Un código usado por un envío se interpreta contra la publicación de reglas asociada a ese envío. Retirar un motivo en una publicación nueva lo hace no seleccionable en envíos nuevos, pero no elimina ni altera su descripción/código en versiones anteriores. En borradores se conserva el código: se retira marcando `Disponible=false`, no borrándolo. No se borra físicamente una publicación.
6. **Devolución por etapas.** “Permitir” significa que la política habilita una decisión; “solicitar” es registrar una intención y no cambia estado; “iniciar” es una transición explícita a `EnDevolucion`; “devuelto” solo ocurre cuando CU-22 completa recepción/rendición. En modo manual no se crea por sí sola una solicitud ni un evento de devolución. En modo automático se inicia el flujo al aceptar la última falla, no un job diferido. Ningún motivo particular causa devolución automática salvo que un caso posterior lo especifique.
7. **Plazo frente a franja.** El plazo es una duración límite; no es una franja semanal ni una promesa horaria concreta. CU-02 define oferta semanal por zona en `America/Montevideo`; al crear una promesa, CU-07 debe elegir una ventana concreta compatible, conservar sus instantes/offsets y asegurar que no exceda el límite. Planificación CU-13/CU-14 debe respetar tanto la franja comprometida como el plazo. Las horas son corridas; no hay calendario hábil, feriados ni pausas. No se duplica una zona horaria: se conserva la de la revisión de zona seleccionada.
8. **Modalidades.** El borrador almacena `PlazoEstandarMinutos` y `PlazoUrgenteMinutos` como campos explícitos; no depende del enum `ModalidadTarifaria`, que vive en el agregado de precios. El consumidor futuro relacionará su modalidad elegida con el plazo correspondiente, sin duplicar una definición de modalidad ni hacer que el agregado de reglas dependa de Tarifas.

## 4. Modelo de dominio y publicación

Propuesta mínima, separada del agregado tarifario:

```text
Operador 1 ── 1 ReglasOperativasOperador
                    │ VersionVigenteId
                    ├── 0..1 VersionReglas (Borrador)
                    └── 0..N VersionReglas (Publicadas e inmutables)
                              ├── 0..N RequisitoPruebaEntrega (enum cerrado)
                              └── 1..N MotivoNoEntregaVersion (código/descr./disponible)

Envio (futuro) ── OperadorId + VersionReglasId ──> publicación inmutable
```

| Entidad / tabla propuesta | Campos relevantes y función |
|---|---|
| `ReglasOperativasOperador` | `OperadorId` PK/FK, `VersionVigenteId` nullable, `UltimoNumeroPublicacion`, `ActualizadoUtc`. Una fila por operador; el contador da números sin `MAX+1`; la raíz serializa operaciones. |
| `VersionesReglasOperativas` | PK compuesta `(OperadorId, Id)`; `Estado` (`Borrador`/`Publicada`); `VersionConcurrencia long`; `NumeroVersionPublicada int?`; autor y fechas de creación/publicación; campos nullable `MaxIntentosEntrega`, `IntervaloEntreIntentosMinutos`, `PoliticaDevolucionAlAgotarIntentos`, `PlazoEstandarMinutos`, `PlazoUrgenteMinutos`. Nulls permiten un borrador inicial/incompleto; no hay valores operativos por defecto. La versión concentra los dos plazos fijos; no requiere una tabla genérica de configuración. |
| `RequisitosPruebaEntrega` | PK `(OperadorId, VersionId, Tipo)`; un registro por tipo enum exigido. Permite cero duplicados e integridad al padre. La validación de publicación requiere al menos uno. |
| `MotivosNoEntregaVersion` | PK `(OperadorId, VersionId, Codigo)`; `Descripcion`, `Disponible`. El mismo operador puede versionar catálogo y conservar códigos anteriores sin mantener una relación con la zona o comercio. |

Cada versión tiene `OperadorId`; las relaciones a operador, versión y futuros envíos usan claves foráneas compuestas `(OperadorId, Id)`. Esto impide enlazar un motivo/hijo o envío con reglas de otro operador. Los tipos de evidencia, estado de versión, modalidad y política se validan con enums cerrados y restricciones SQL equivalentes.

**Estados:** inexistente (aún no configurado), `Borrador` y `Publicada`. Solo una publicación es vigente según el puntero de la raíz; publicaciones previas permanecen `Publicada` como históricas. No se añade un estado “expirada” o “archivada” que no aporta al tratamiento histórico.

### Reglas de publicación

- Un operador puede tener como máximo un borrador. La primera creación inicia vacío; las siguientes copian los valores, requisitos y catálogo de la publicación vigente. Editar el borrador no modifica el histórico.
- Para publicar se exige: valores válidos; máximo e intervalo positivos; dos plazos positivos; una política; al menos un tipo de prueba; al menos un motivo disponible; códigos únicos; y límites de texto. Las versiones publicadas solo contienen contratos cerrados conocidos.
- Si se publica por primera vez, no existe una publicación anterior que pueda aplicarse como fallback. La app debe mostrarlo claramente y bloquear consumidores que necesiten reglas hasta que haya publicación.
- No se requieren tablas de intentos, archivos probatorios, devoluciones, calendario ni outbox en CU-04.

## 5. Concurrencia e historial

Se reutiliza el protocolo tarifario con un agregado distinto:

- **Versión de concurrencia** (`VersionConcurrencia`, `long`): empieza en 0 para el borrador y se incrementa con cada cambio aceptado. Toda edición/publicación manda la versión leída; una obsoleta produce conflicto de aplicación (futuro HTTP 409) sin escrituras parciales.
- **Número publicado** (`NumeroVersionPublicada`, `int?`): se asigna solo al publicar, monotónico por operador. Identifica una revisión comercial/histórica; no detecta conflictos de edición.
- Edición, copia y publicación abren o se unen a la transacción RLS existente y toman el mismo `FOR UPDATE` sobre `ReglasOperativasOperador`. El primer insert de la raíz usa conflicto único seguido del mismo bloqueo, para serializar también la primera creación. Bajo ese bloqueo se valida versión, valores e hijos. Al publicar se guarda primero la transición de versión y luego el puntero/contador de la raíz dentro de la misma transacción; cualquier fallo revierte ambos cambios. Así se serializa con ediciones/copia y dos publicaciones no pueden quedar vigentes ni repartir el mismo número.
- El modelo EF conserva token de concurrencia, clave compuesta, índice único de un borrador por operador y de número publicado por operador. `ENABLE`/`FORCE ROW LEVEL SECURITY`, `USING`/`WITH CHECK`, privilegios mínimos y triggers persistentes protegen cabeceras publicadas y sus requisitos/motivos hijos contra `UPDATE`/`DELETE`. Los requisitos no conceden `UPDATE` al rol API; los códigos de motivos son inmutables y se desactivan en vez de borrarse.

### Asociación futura al envío

CU-07, al admitir un envío y después de conocer el operador y modalidad seleccionados para ese envío, resolverá la publicación de reglas vigente una sola vez y guardará en la misma transacción `OperadorId + VersionReglasId`, modalidad, `AdmitidoUtc` y `ComprometidoHastaUtc`. CU-09 guardará separadamente `VersionTarifarioId` y el importe calculado. Ningún caso debe resolver “la versión vigente ahora” al volver a procesar un envío ya admitido.

La FK a una publicación inmutable conserva máximos, intervalo, pruebas, motivos y política que aplican; los eventos futuros guardan la versión, códigos seleccionados y timestamps que efectivamente usaron. El plazo absoluto y la ventana concreta de CU-02 se guardan como compromiso del envío, no se recalculan desde una nueva duración o franja. CU-11/CU-20/CU-21/CU-22/CU-26 leen la versión referenciada, no la raíz vigente.

`Envio` no tiene hoy estos campos. La integración y el cambio de modelo corresponden a CU-07/CU-09 y CU-11, no a este corte. Los envíos existentes se dejan intactos y no reciben una publicación automáticamente; su compatibilidad se resolverá al integrar el consumidor correspondiente, antes de procesarlos con reglas nuevas. Esta decisión futura no bloquea la tabla de reglas porque la migración de esta fase no modifica `Envio`.

## 6. Autorización y RLS

| Actor / contexto | Consultar configuración propia | Crear/editar borrador | Publicar | Leer reglas para operar un envío |
|---|---:|---:|---:|---:|
| Operador activo con permiso vigente `configuracion.gestionar` | Sí | Sí | Sí | Solo para su operador/envío |
| Usuario de Operador sin permiso o acceso revocado | No | No | No | Solo si el futuro caso de uso permite esa acción operacional por separado |
| Otro Operador | Solo sus propias filas | Solo sus filas | Solo sus filas | Solo un envío propio |
| Commerce, aunque posea permiso homónimo | No | No | No | El flujo del envío solo consulta la versión capturada en el envío según su autorización; nunca administra reglas |
| Plataforma | Sin acceso operativo general | No | No | Solo herramientas administrativas explícitas ya autorizadas; CU-04 no se las concede |
| Anónimo, identidad sin organización o suspendida | No | No | No | No |

El operador se obtiene de `ContextoUsuario` revalidado en el pipeline; no hay `OperadorId` de formulario. La capa de aplicación exige acceso persistido activo de tipo Operador **y** permiso dinámico. No se autoriza por nombre de rol. `RlsTransactionMiddleware` revalida acceso/sesión y aplica el contexto dentro de su transacción; los casos de uso usan la transacción existente.

Todas las tablas del agregado tienen `ENABLE` y `FORCE ROW LEVEL SECURITY`, propietario separado del rol runtime, políticas por `nexoruta.org_id` y actor `operador`, sin `SELECT` global. Las políticas de escritura permiten modificar únicamente borradores propios; hijos requieren el mismo `OperadorId`/`VersionId`; una publicación y sus hijos no admiten modificación o borrado aunque se intente mediante una consulta API. La lectura operacional futura será un query acotado a la versión que ya referencia un envío del mismo operador, no un permiso general de lectura de todas las reglas ni una conexión privilegiada.

## 7. Recorrido previsto en Backoffice

1. Un administrador de Operador activo entra a **Configuración → Reglas operativas**. Si no hay publicación, ve “Todavía no hay reglas publicadas” y puede crear el primer borrador.
2. El borrador organiza los campos por negocio: **Intentos y espera**, **Prueba de entrega**, **Motivos de no entrega**, **Devoluciones** y **Plazos por modalidad**. La pantalla muestra valores y descripciones; los GUID internos y el token de concurrencia no se presentan como datos de negocio.
3. Para actualizar reglas vigentes, se ofrece **Crear copia para editar**. El operador conserva todas las razones y requisitos; puede cambiar disponibilidad o valores, y revisar el resumen. Un código técnico no cambia al editar una descripción.
4. **Guardar borrador** y **Publicar reglas** son acciones distintas. Publicar valida el conjunto, muestra el resumen y pide confirmación; al confirmarse la publicación queda de solo lectura y la anterior sigue consultable. Para cambiarla se crea otra copia.
5. Si otra persona guardó primero, la versión obsoleta muestra un conflicto y ofrece recargar explícitamente; no reintenta con un token nuevo ni reemplaza cambios ajenos.

La interfaz no contiene lógica de transición de envíos ni registro de evidencias. Su único efecto es configurar una versión.

## 8. Casos consumidores, aceptación y pendientes

### Responsabilidad por caso de uso

| Caso | Consume de CU-04 | No es trabajo de CU-04 |
|---|---|---|
| CU-07 Alta de envíos | Resolver publicación al admitir, modalidad y plazo concreto en la misma transacción, cuando queden acordados y persistidos. | Cálculo tarifario (CU-09) y selección de una nueva regla para un envío ya creado. |
| CU-11 Estado del envío | Version de reglas asociada; máquina explícita, transiciones e intervalo/máximo; decisión configurada tras el último intento. | Configurar las reglas o editar una publicación. |
| CU-12 Eventos | Eventos inmutables y outbox transaccional cuando un cambio de estado lo requiera. | Reintentos de entrega de mensajes no son intentos de reparto. |
| CU-20 Entrega | Exigir los tipos de evidencia y registrar hora/posición conforme a la versión asociada. | Captura móvil, compresión, almacenamiento/sincronización offline. |
| CU-21 Intento fallido | Elegir motivo vigente para la versión del envío, guardar evidencia y entregar un solo evento idempotente a CU-11. | El catálogo y contador de reglas. |
| CU-22 Devolución y rendición | Interpretar resultado de política e iniciar/registrar devolución conforme al flujo autorizado. | Transporte físico y rendición al depósito no se ejecutan al publicar reglas. |
| CU-26 Reprogramación | Validar límite, espera y plazo aplicables al envío, sin reiniciar intentos. | Definir cupos, feriados, calendario laboral o disponibilidad futura por fecha. |
| CU-13/CU-14 y CU-29 | Respetar la franja/ventana concreta y el límite comprometido; detectar riesgo. | Reescribir franjas de CU-02 o planificar rutas desde CU-04. |

### Criterios de aceptación propios de CU-04

- Un operador sin configuración puede crear un borrador inicial incompleto; no hay valores operativos ficticios ni publicación implícita.
- Dos operadores tienen agregados aislados. La administración exige el contexto persistido de Operador y `configuracion.gestionar`; Commerce, Plataforma y contexto ausente no leen ni escriben las tablas.
- Valores escalares fuera de límites, enums desconocidos, requisitos repetidos, códigos mal formados/repetidos y textos inválidos son rechazados. Los códigos se introducen explícitamente, se conservan al copiar, no cambian al editar descripciones y no se eliminan: se marcan no disponibles.
- Publicar exige todos los campos escalares, política conocida, al menos un requisito y un motivo disponible. Un borrador puede guardarse incompleto.
- Una publicación queda inmutable con todos sus hijos; el siguiente borrador es independiente y conserva valores, requisitos, códigos, descripciones y disponibilidad.
- La versión de concurrencia cambia en cada edición y es independiente del número publicado. Una versión obsoleta no cambia cabecera ni hijos.
- La primera creación, edición/copia y publicación se serializan por la raíz; la publicación y el puntero vigente se confirman o revierten juntos.
- La cadena de migraciones desde vacío y desde el esquema anterior funciona; las pruebas RLS usan el rol API restringido, no solo el propietario/superusuario.

### Escenarios de casos consumidores futuros (no aceptación de esta fase)

- CU-20 valida que todos los tipos de prueba exigidos estén presentes; si falta uno, no acepta el estado `Entregado`.
- CU-21 registra una falla y motivo del catálogo de la versión vinculada al envío. CU-11 cuenta visitas; al alcanzar el máximo aplica la política, y CU-22 completa el flujo físico de devolución.
- CU-26 valida el intervalo y plazo de una solicitud de reprogramación sin aumentar intentos ni recalcular una publicación histórica.
- CU-24 resuelve timestamps discrepantes, eventos offline y orden de sincronización; CU-04 no crea visitas artificiales ni procesa eventos.

### Decisiones abiertas reales de integración posterior

1. **Reloj y eventos offline (CU-24):** el laboratorio no fija tolerancia a deriva ni cuál instante es confiable cuando el dispositivo está offline. CU-24 debe resolver validación y orden/conflicto antes de registrar un intento. No bloquea el almacenamiento de reglas de CU-04.
2. **Compatibilidad de envíos existentes (CU-07/CU-11):** los envíos actuales se conservan sin cambios y no se les asigna la primera versión automáticamente. La política de lectura/operación legado debe definirse antes de integrar consumidores; no bloquea esta migración, que no modifica `Envio`.

Las pruebas AND, el intervalo hacia el siguiente intento, límites técnicos, versión inicial incompleta y código explícito quedaron resueltos para el modelo de esta fase. Los valores operativos del operador se completan en su borrador; no son defaults globales.

### Estado de la Fase 2

**Implementado:** agregado de dominio en `Domain/ReglasOperativas` (`ReglasOperativasOperador`, `VersionReglasOperativas`, `RequisitoPruebaEntrega`, `MotivoNoEntregaVersion`); casos de uso de consulta, borrador inicial/copia, edición completa y publicación en `Application/ReglasOperativas`; repositorio EF en `Infrastructure/ReglasOperativas/EfReglasOperativasRepository`; validación de códigos explícitos `A-Z`/`0-9`/`_`, motivo no borrable y desactivación versionada. El agregado no depende de `ModalidadTarifaria`; conserva los plazos explícitos Estándar/Urgente.

**Persistencia implementada:** migración `20261019235212_AddReglasOperativas` crea `ReglasOperativasOperador`, `VersionesReglasOperativas`, `RequisitosPruebaEntrega` y `MotivosNoEntregaVersion`, con claves compuestas, límites, índices únicos parciales, RLS forzada, propietario `nexoruta_schema_owner`, permisos mínimos y triggers SECURITY DEFINER con `search_path` fijo y EXECUTE revocado a PUBLIC/runtime. La migración no fue aplicada a PostgreSQL persistente. El guard de publicación valida el conjunto completo; los hijos publicados no se pueden editar y los códigos no se actualizan/eliminan.

**Verificado:** `dotnet test tests/NexoRuta.UnitTests/NexoRuta.UnitTests.csproj --configuration Release --filter FullyQualifiedName~ReglasOperativas --no-restore` (6/6); `dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --configuration Release --filter FullyQualifiedName~ReglasOperativasPersistenceTests --no-restore` (4/4, Testcontainers `postgis/postgis:18-3.6-alpine`, almacenamiento efímero). La integración cubre migración desde vacío y desde `20261019152350_AddTarifarioOperador`, rol API restringido, ausencia de contexto, actores Commerce/Plataforma, dos operadores, publicación/copias/inmutabilidad, restricciones de privilegios y `SECURITY DEFINER`, referencias cruzadas, primera creación concurrente, carrera edición/publicación y rollback completo al fallar la actualización del puntero de publicación. `dotnet build src/NexoRuta.Api/NexoRuta.Api.csproj --configuration Release --no-restore` terminó con 0 warnings y 0 errors; `dotnet ef migrations has-pending-model-changes --project src/NexoRuta.Infrastructure/NexoRuta.Infrastructure.csproj --startup-project src/NexoRuta.Api/NexoRuta.Api.csproj --configuration Release` informó que no hay cambios pendientes del modelo; `git diff --check` no reportó errores. La migración se probó solo en las bases efímeras y no se aplicó al PostgreSQL persistente.

**Al cierre de la Fase 2, pendiente:** controladores HTTP, DTOs, ApiClient, OpenAPI, Backoffice y validación integrada local; no se modificaron ni probaron envíos, intentos, evidencias, devoluciones o sincronización.

**Siguiente fase:** implementar la interfaz Backoffice y validar el recorrido local; la asociación de reglas al crear envíos sigue reservada a CU-07/CU-11, fuera de esta entrega.

## Estado de la Fase 3: API, ApiClient y OpenAPI

### Rutas y contratos HTTP implementados

| Método y ruta | Resultado |
|---|---|
| GET /api/configuracion/reglas-operativas | Estado normal del operador: configurado y resúmenes nullable de publicación vigente y borrador. Sin configuración, responde configurado=false y referencias null; el GET no crea datos. |
| POST /api/configuracion/reglas-operativas/borrador | Crea un borrador inicial vacío o copia la publicación vigente, según el estado del agregado. Responde 201 Created, cuerpo completo y Location. |
| GET /api/configuracion/reglas-operativas/borrador | Devuelve el borrador completo; 404 si no existe. |
| PUT /api/configuracion/reglas-operativas/borrador | Reemplaza atómicamente escalares, requisitos y motivos usando versionConcurrencia; devuelve el agregado completo y la nueva versión. |
| POST /api/configuracion/reglas-operativas/borrador/publicar | Recibe versionConcurrencia; valida y publica mediante el caso de uso existente. Devuelve la publicación completa. |
| GET /api/configuracion/reglas-operativas/publicaciones?pagina=1&tamanoPagina=20 | Historial paginado y ordenado por número publicado descendente; tamaño máximo 100. |
| GET /api/configuracion/reglas-operativas/publicaciones/{versionId} | Detalle inmutable de una publicación propia; IDs ajenos o inexistentes responden 404. |

El reemplazo PUT exige que aparezcan versionConcurrencia, los cinco escalares nullable, requisitosPrueba y motivos. Un valor nullable explícitamente null conserva un borrador incompleto; omitir una propiedad requerida es un error de contrato. Las colecciones vacías son distintas de colecciones omitidas. Requisitos no se duplican; los códigos de motivos se envían explícitamente y no se regeneran desde la descripción. Si el reemplazo intenta retirar un motivo existente, el dominio rechaza toda la operación; no se borra ni desactiva silenciosamente. Para retirar la disponibilidad se envía el motivo con disponible=false.

Los enums se representan como cadenas JSON y los valores numéricos/desconocidos se rechazan. El contrato no recibe operador, actor, estado, número de publicación ni fechas del servidor. versionConcurrencia es requerido y acepta cero. El borrador puede permanecer incompleto, pero los escalares presentes fuera de rango se rechazan; publicar requiere escalares completos, política, al menos una prueba y un motivo disponible. Los errores de validación controlados incluyen código y campos; conflictos de versión/estado son 409. Ningún controlador reproduce bloqueos ni publica directamente.

Valores cerrados expuestos: requisitos de prueba FirmaEnPantalla, Fotografia, NombreReceptor y DocumentoReceptor; política DecisionOperativa o IniciarAutomaticamente; estado de lectura Borrador o Publicada. Los límites HTTP son máximo 100 intentos, 525600 minutos por intervalo/plazo, cuatro requisitos, 100 motivos, código de motivo de 1–40 caracteres (`^[A-Z][A-Z0-9_]*$`) y descripción de 1–120 caracteres. El reemplazo se limita a 65536 bytes; el historial admite páginas de 1–100 elementos, por defecto 20. Los errores controlados distinguen borrador/publicación inexistente, solicitud/configuración inválida, configuración incompleta y conflicto del borrador; la API responde 400, 401, 403, 404 o 409 según corresponda.

La administración reutiliza las políticas actuales de sesión de organización y configuracion.gestionar, y los casos de uso derivan operador y actor del contexto persistido. Commerce y Plataforma no obtienen acceso administrativo. No se agregó lectura operacional para consumidores futuros ni se expuso contexto RLS desde HTTP.

### ApiClient

NexoRuta.ApiClient.Features.ReglasOperativas contiene contratos frontend independientes del backend y ReglasOperativasApiClient. El cliente usa ApiHttp, Bearer por solicitud, CancellationToken y el tratamiento compartido de errores. Se registra únicamente en Backoffice; no replica validación ni publicación.

### OpenAPI

Se regeneró frontend/contracts/nexoruta.openapi.json desde el documento servido por la API durante la prueba de exportación. El snapshot incluye las rutas anteriores, respuestas de error, enums string, nulabilidad explícita de borradores, propiedades requeridas, límites y paginación. La prueba de contrato valida estas formas contra el OpenAPI generado, no contra bloques editados manualmente.

### Verificado en esta fase

- dotnet build src/NexoRuta.Api/NexoRuta.Api.csproj --configuration Release --no-restore: correcto, 0 warnings y 0 errores.
- dotnet build src/NexoRuta.Backoffice/NexoRuta.Backoffice.csproj --configuration Release --no-restore: correcto, 0 warnings y 0 errores.
- dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --configuration Release --filter FullyQualifiedName~ReglasOperativasHttpTests --no-restore: 3/3; Testcontainers/PostgreSQL-PostGIS efímero y rol API restringido. Incluye GET sin escrituras, borrador vacío e incompleto, publicación/copia/historial, validaciones estrictas, conflicto obsoleto sin cambio, aislamiento, IDs ajenos y matriz de acceso.
- dotnet test tests/NexoRuta.UnitTests/NexoRuta.UnitTests.csproj --configuration Release --filter FullyQualifiedName~ReglasOperativas --no-restore: 6/6, incluida la lista identificable de campos incompletos al publicar.
- dotnet test tests/NexoRuta.ApiClient.Tests/NexoRuta.ApiClient.Tests.csproj --configuration Release --no-restore --filter FullyQualifiedName~ReglasOperativasApiClientTests|FullyQualifiedName~OperationalRulesContractContainsVersionedDraftPublicationAndValidationSchemas: 3/3; rutas, Bearer, enums, nulls, concurrencia cero, paginación, respuesta tipada y conflicto.
- La prueba ReglasOperativasHttpTests.OpenApiContainsTypedRulesRoutesAndExportsTheGeneratedDocument exportó el OpenAPI de ejecución al snapshot versionado.
- Las pruebas HTTP usaron almacenamiento efímero. No se aplicaron migraciones ni se accedió al PostgreSQL persistente de Compose. No hubo cambios de modelo EF ni migraciones nuevas.

### Pendiente

Backoffice aún no presenta ni edita reglas, y esta fase no ejecutó recorrido integrado local. La migración 20261019235212_AddReglasOperativas permanece sin aplicar a la base persistente. Aplicación de reglas a envíos, intentos, pruebas/evidencias, devoluciones y sincronización siguen fuera de alcance; corresponden a CU-07/CU-11/CU-20/CU-21/CU-22/CU-24/CU-26 según el consumidor.

## Estado de la Fase 4 — Backoffice

### Implementado

- Rutas Razor: `/reglas-operativas`, `/reglas-operativas/borrador`, `/reglas-operativas/publicar`, `/reglas-operativas/publicaciones` y `/reglas-operativas/publicaciones/{versionId}`.
- Navegación operativa compartida, incluidas las páginas que usan `_StandaloneZonesNavigation`. Los enlaces y todas las páginas reutilizan el requisito persistido de operador y el permiso dinámico `configuracion.gestionar`; no se autoriza por nombre de rol. El ApiClient continúa enviando el Bearer desde el servidor y el JWT no se renderiza en el navegador.
- Inicio que separa publicación vigente, borrador e historial; creación inicial o copia solo tras POST explícito; número publicado como dato de negocio, sin mostrar IDs ni versión de concurrencia como información principal.
- Editor completo por secciones: intentos, pruebas acumulativas, motivos (incluidos los no disponibles), política al agotar intentos y plazos Estándar/Urgente. Los códigos existentes se muestran de solo lectura y se siguen enviando; se pueden editar descripción/disponibilidad, pero no quitar motivos persistidos. Motivos nuevos se pueden quitar localmente antes de guardar.
- Los reemplazos envían versión de concurrencia y las dos colecciones completas, incluso vacías. Un guardado exitoso redirige al GET del borrador para recuperar la versión vigente. No hay autoguardado.
- Intervalo y plazos se editan en días, horas y minutos. Vacío completo se envía como `null`; componentes parcialmente vacíos equivalen a cero en esa duración; una duración total cero o superior a 525600 minutos se rechaza. La conversión usa enteros y conserva exactamente los minutos admitidos por la API.
- Publicación con resumen, validación visible de secciones incompletas, confirmación expresa y POST separado; no guarda el borrador ni ejecuta devoluciones o cambios sobre envíos. El resultado redirige a una publicación de solo lectura; el detalle permite crear copia únicamente si no existe borrador.
- Historial paginado por API, con indicador de vigente/anterior y etiquetas de pruebas y políticas centralizadas en `ReglasOperativasUi`.
- Antiforgery en POST, `data-single-submit`, preservación del formulario ante errores 400/409, oferta de recarga explícita para conflictos y manejo existente de sesión 401, acceso 403, recurso 404 y fallos de transporte. Los handlers de filas locales no ejecutan validaciones de publicación ni guardan la configuración.

### Rutas de interfaz

| Ruta | Uso |
|---|---|
| `/reglas-operativas` | Estado vigente, cambios en preparación, acciones iniciales e historial. |
| `/reglas-operativas/borrador` | Editor de reemplazo completo y controlado por versión. |
| `/reglas-operativas/publicar` | Resumen y confirmación explícita de publicación. |
| `/reglas-operativas/publicaciones` | Historial paginado y estado vigente/anterior. |
| `/reglas-operativas/publicaciones/{versionId}` | Detalle de solo lectura y acción de copia cuando corresponde. |

### Verificado en esta fase

- `dotnet build src/NexoRuta.Backoffice/NexoRuta.Backoffice.csproj --configuration Release --no-restore`: correcto, 0 errores; 3 advertencias CS8629 preexistentes en `Features/ZonasCobertura/Pages/CrearModel.cs` y `DetalleModel.cs`.
- `dotnet test tests/NexoRuta.ApiClient.Tests/NexoRuta.ApiClient.Tests.csproj --configuration Release --no-restore --filter 'FullyQualifiedName~OperationalRules|FullyQualifiedName~ZoneNavigationAndPagesRequireCurrentOperatorPermission|FullyQualifiedName~TariffHomeShowsUnconfiguredStateOnlyToAuthorizedOperators' --logger 'console;verbosity=minimal'`: 21/21. Incluye Razor Pages con doble HTTP para autorización y navegación, borradores incompletos/null, conversión de duraciones incluyendo 1501 y 525600 minutos, rechazo de cero y de 525601, colecciones completas, requisitos seleccionados, disponibilidad false, códigos estables, alta/eliminación local, 400/401/403/404/409, antiforgery, confirmación sin PUT implícito, historial y detalle de solo lectura.
- Las pruebas usan el fixture `BackofficeFactory` y `FakeApi`; no son integración con API, PostgreSQL, RLS ni migraciones reales.
- Se reutilizó `wwwroot/js/tarifas-editor.js` para aviso de cambios pendientes, confirmación de eliminación local y navegación a publicar solo con formulario limpio; no se modificó JavaScript, por lo que no aplicó `node --check`.
- No se usó navegador, Compose ni la base persistente. Edge y Chrome están instalados, pero no hay Playwright/WebDriver disponible; el intento de iniciar un host local con API simulada para un recorrido visual fue rechazado por la política del proceso de comandos, por lo que no se obtuvieron capturas ni se reporta validación visual escritorio/móvil. No se aplicaron migraciones.

### Pendiente

La interfaz queda implementada. La aplicación de intentos, pruebas/evidencias, devoluciones, sincronización y cualquier asociación de reglas a envíos continúa fuera de este corte.

## Estado de la Fase 5 — instalación local y auditoría integrada

### Destino y migraciones

- Destino confirmado: proyecto Compose `nexoruta`, servicio/contenedor `postgres`/`nexoruta-postgres`, base `nexoruta`, PostgreSQL 18.6 en `postgis/postgis:18-3.6-alpine`; se conservó el volumen existente `nexoruta_postgres_data` montado en `/var/lib/postgresql` con `PGDATA=/var/lib/postgresql/18/docker`. No se hizo reset, backup ni se borraron volúmenes.
- La única migración pendiente al inicio fue `20261019235212_AddReglasOperativas`; se aplicó con el perfil `rls-admin` y el migrador autorizado. La inicialización habitual sigue con `DatabaseInitialization__ApplyMigrations=false` y `DatabaseInitialization__SeedData=false`; no se ejecutaron seeders ni bootstrap general.
- El flujo HTTP descubrió un defecto real en el trigger de protección de hijos. Como la migración inicial ya estaba aplicada y el reset estaba prohibido, se agregó y aplicó la migración correctiva `20261020021506_FixOperationalRulesChildGuard`. El historial contiene ambas migraciones. Esta corrección no cambia el modelo EF; `dotnet ef migrations has-pending-model-changes` informa que no hay cambios pendientes.

### Esquema y seguridad observados

- Existen `ReglasOperativasOperador`, `VersionesReglasOperativas`, `RequisitosPruebaEntrega` y `MotivosNoEntregaVersion`. Las cuatro tienen `ENABLE ROW LEVEL SECURITY` y `FORCE ROW LEVEL SECURITY`; su propietario es `nexoruta_schema_owner`, separado de la API.
- Se observaron claves foráneas compuestas operador/versión, restricciones de nulabilidad y rangos, códigos de motivo únicos por versión, un índice parcial para un borrador por operador y unicidad del número publicado por operador. Los triggers cubren raíz, versión e hijos; las publicaciones y sus colecciones quedan protegidas frente a cambios.
- Las funciones de protección son `SECURITY DEFINER`, propiedad de `nexoruta_schema_owner` y tienen `search_path=pg_catalog, public`; `EXECUTE` está revocado a `PUBLIC` y a los roles runtime. Los roles `nexoruta_api`, `nexoruta_api_runtime`, `nexoruta_worker` y `nexoruta_bootstrap` no son superuser ni tienen `BYPASSRLS`. La API runtime conserva solo los privilegios de tabla requeridos por el CRUD del borrador/publicación; el worker no tiene lectura de estas tablas. Las políticas son específicas al operador contextual. La matriz de actores y el aislamiento cruzado no se repitieron en esta fase; se conserva como evidencia previa la prueba HTTP/RLS de fase 3.

### Corrección encontrada durante el recorrido

Al reemplazar requisitos de prueba de un borrador, PostgreSQL devolvía HTTP 500 (`record "new" has no field "Codigo"`). La función trigger compartida entre las tablas de requisitos y motivos intentaba leer la columna `Codigo` en una fila de `RequisitosPruebaEntrega`, donde no existe. La operación fallida no dejó escrituras parciales. `20261020021506_FixOperationalRulesChildGuard` limita la comprobación de código al caso de motivos y evita acceder a una columna ausente en la otra tabla. Se agregó una regresión de persistencia que reemplaza requisitos y motivos con valores vacíos/incompletos y comprueba conservación atómica. Se compiló el proyecto de integración, pero esa prueba no se ejecutó con Testcontainers porque esta fase autorizó escrituras únicamente en el PostgreSQL local de Compose.

### Actualización de servicios y evidencia HTTP

- Se reconstruyeron y recrearon únicamente `api` y `backoffice` con `compose.yaml` + `compose.dev.yaml`, sin dependencias (`--no-deps`). PostgreSQL, workers, brokers y otros portales se conservaron. API responde `200` en readiness; el OpenAPI servido contiene las cinco rutas de reglas; Backoffice usa `Api__BaseAddress=http://api:8080/`.
- Con el operador local autorizado existente, el recorrido real por HTTP comprobó el estado sin configuración, alta del borrador, guardado incompleto con `null` reales y colecciones vacías, lectura posterior, configuración completa, respuesta 400 al intentar publicar la configuración incompleta y persistencia sin incremento ni modificación indebida. El formulario de Backoffice guardó luego la configuración completa con intervalos exactos en minutos.
- Se guardó y verificó un motivo no disponible sin perder su código, otro disponible, ambos requisitos de prueba seleccionados, la política de decisión por responsable y los plazos Estándar/Urgente. Dos capturas del mismo borrador con versión 2 simularon pestañas concurrentes: la primera guardó; la segunda recibió el conflicto, conservó el valor ingresado y la versión obsoleta, sin reintentar ni sobrescribir. La versión persistida resultó 3.
- Desde la página de confirmación de Backoffice se publicó explícitamente la versión 3. La API y el historial muestran las publicaciones 1, 2 y 3, con la 3 vigente. El detalle v2 conserva `DIRECCION_INCORRECTA` disponible y su descripción original; v3 conserva el mismo código con disponibilidad `false` y la nueva descripción, demostrando que editar la copia no alteró el histórico. v1 y v2 son datos de demostración locales; v2 quedó como una publicación de demostración duplicada durante un intento previo de flujo que no completó el guardado incompleto. No se reemplazó configuración preexistente: la raíz estaba inicialmente vacía.
- La página principal, borrador, historial y detalle devolvieron `200` con sesión autenticada. Las páginas independientes `/usuarios`, `/roles`, `/zonas` y `/tarifas` también devolvieron `200` y renderizaron el enlace de reglas. El CSS de la feature respondió `200`; no se detectó la etiqueta prohibida “tarifario de reglas”. El POST de publicación no creó envíos ni outbox: al cierre las tablas `Envios` y `OutboxMessages` siguen en 1 y 18 filas respectivamente, los mismos conteos observados antes/después de la publicación inicial. CU-04 no registra intentos, inicia devoluciones ni crea eventos o trabajos operativos.
- Datos de demostración actuales: publicación 3 vigente; sin borrador abierto. Máximo 3 intentos, intervalo mínimo 1.440 minutos, requisitos `FirmaEnPantalla` y `Fotografia`, política `DecisionOperativa`, plazos Estándar 4.320 minutos y Urgente 1.440 minutos. `DOMICILIO_CERRADO` está disponible; `DIRECCION_INCORRECTA` se conserva no disponible. Las descripciones están identificadas como demo CU-04. No se modificaron envíos, identidades, roles, permisos o credenciales.

### Builds, advertencias y límites de validación

- `dotnet build src/NexoRuta.Api/NexoRuta.Api.csproj --configuration Release --no-restore`: correcto, 0 advertencias y 0 errores.
- `dotnet build tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --configuration Release --no-restore`: correcto, 0 advertencias y 0 errores; compilación solamente, no ejecución de Testcontainers.
- `dotnet build src/NexoRuta.Backoffice/NexoRuta.Backoffice.csproj --configuration Release --no-restore`: correcto, 0 advertencias y 0 errores.
- Las tres advertencias `CS8629` de CU-02 informadas en la sesión anterior estaban en `Features/ZonasCobertura/Pages/CrearModel.cs` (líneas 94 y 120) y `Features/ZonasCobertura/Pages/DetalleModel.cs` (línea 75). Esos archivos no cambiaron en CU-04/Fase 5; no aparecen en los builds Release de esta ejecución y no se refactorizaron.
- No se dispuso de controlador automatizable de navegador. Los estados anteriores son pruebas HTTP reales contra API y Backoffice, no una revisión visual. No se declara aprobado el diseño responsivo ni el comportamiento interactivo de advertencia al abandonar, doble envío o confirmación en navegador. Las pruebas de página con `BackofficeFactory`/`FakeApi` de fase 4 siguen siendo evidencia separada y no se presentan como integración real. No se ejecutaron suites completas ni bases Testcontainers externas.

### Auditoría de alcance y siguiente comprobación

La auditoría conserva la trazabilidad ya documentada: Laboratorio §5.1 requisito 4 define la configuración de máximo e intervalo de intentos, prueba, motivos, política de devolución y plazos; §5.2 requisitos 11–12 y §5.4 requisitos 20–22 describen consumidores de estados/evidencias/devoluciones; §5.5 requisito 26 trata reprogramación; §§6.4–6.6 exigen concurrencia, aislamiento multitenant y edición/versionado histórico. En `docs/alcance/Calcs/Casos de Uso - Responsables.xlsm`, `CU obligatorios!A7:I7` asigna CU-04 al operador/Backoffice y `Dependencias!A6:I6`, `A13:I14` y `A19:I19` registran dependencias hacia CU-11, CU-20, CU-21 y CU-26. La hoja se inspeccionó sin macros; la ruta real conserva la `C` mayúscula de `Calcs`.

CU-04 queda acotado a configurar, guardar borradores, publicar y consultar historial. La integración de reglas con admisión/entrega/compromisos y snapshots de envíos queda para CU-07/CU-11/CU-20/CU-21/CU-22/CU-24/CU-26 según el consumidor; aquí no se modificó `Envio`, no se registran intentos ni evidencias, no se procesan devoluciones ni sincronización offline, y no se añadió relación comercio–operador. Para validación manual: abrir `http://localhost:5101/reglas-operativas` con la cuenta local autorizada, revisar publicación vigente v3 e historial, entrar al detalle y comparar v2/v3; para editar, crear explícitamente una copia y guardarla como borrador.

**Pendiente para cerrar CU-04:** revisión manual visual en navegador real en escritorio y móvil, incluyendo advertencia de cambios pendientes, formularios auxiliares y protección de doble envío. Hasta que se realice, CU-04 no se declara cerrado.
