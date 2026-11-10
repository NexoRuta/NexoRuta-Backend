# AP-04 — Consulta y gestión de devoluciones del comercio

Estado: especificación de implementación. Fecha: 09/11/2026. Módulo M02.

## Trazabilidad y alcance

La planilla `docs/alcance/Calcs/Casos de Uso - Responsables.xlsm`, hoja `Alcance aplicaciones`, fila AP-04, define como capacidad obligatoria: «El comercio consulta y gestiona devoluciones de sus envíos». El estado de definición es «Detalle funcional a diseñar» y asigna Portal Blazor, M02 y §4. El PDF de alcance incluye gestión de devoluciones en el portal, limita cada comercio a su propia información, conserva al operador como tenant y exige estados/eventos trazables. Ninguna de esas fuentes prescribe un acuse, aprobación, RMA, reembolso ni plazo de recepción. El acuse descrito aquí completa «gestiona» como una decisión de producto del proyecto.

## Definición funcional adoptada

1. Solo el operador inicia la devolución según sus reglas versionadas y registra recepción de retorno y transferencia física. El permiso `envios.gestionar` sigue siendo exclusivo de Operador.
2. Commerce obtiene una bandeja y detalle por operador, envío y bulto. Puede registrar un acuse inmutable conforme o con observaciones, únicamente sobre bultos con transferencia física nueva y trazable al comercio.
3. Las observaciones son por bulto, obligatorias y acotadas cuando el comercio selecciona «con observaciones». No generan automáticamente extravío, resolución de AP-01, reembolso, pago o liquidación.
4. El acuse no cambia estado/custodia/versión de envío ni bulto, precios, rutas, reservas o hechos físicos. La fecha de registro es UTC del servidor; no se solicita GPS, firma, fotografía ni documento personal.
5. El acuse es definitivo. Una discrepancia posterior requiere otro procedimiento de negocio, no mutación de la evidencia histórica AP-04.
6. Hechos previos al despliegue sin transferencia identificable se muestran como históricos/no acreditados y no permiten acuse. No se hace backfill inferido desde `Devuelto`.

## Matriz de elegibilidad

| Contexto por envío/bulto | Commerce consulta | Commerce acusa | Operador transfiere físicamente |
|---|---:|---:|---:|
| No devolución, o envío de retiro | No en bandeja AP-04 | No | No por este flujo |
| `EnDevolucion` y custodia `Repartidor`/`PendienteDeposito` | Sí, retorno/recepción pendiente | No | No |
| `EnDevolucion` y custodia `Deposito` | Sí, pendiente de entrega física | No | Sí, si selección/versiones corresponden y cuenta con `envios.gestionar` |
| `Devuelto` y custodia `Deposito` | Sí, pendiente de entrega física; conservar resultado histórico | No | Sí solo si el historial canónico demuestra devolución y la transición conserva reglas vigentes |
| `Devuelto` y custodia `Comercio`, con transferencia AP-04 | Sí | Sí, con `devoluciones.gestionar` y acceso activo | Ya transferido |
| Transferencia con acuse existente | Sí, resultado, actor y fecha | No duplicar/editar | No repetir los bultos ya transferidos |
| `Devuelto` histórico con custodia comercio pero sin hecho de transferencia AP-04 atribuible | Sí, histórico/no acreditado | No | No regenerar un hecho actual desde el estado |
| Bulto entregado al destinatario, de retiro, extraviado o cancelado sin elegibilidad demostrable | Mostrar hechos y bloqueo explicativo si hay contexto de devolución | No | No |
| Envío parcial | Sí; resultado/custodia por bulto y agregado real intacto | Solo líneas de transferencia elegibles | Solo selección explícita elegible |
| Versión obsoleta, bulto ajeno, mezcla con selección inválida o reintento incompatible | Sí si el envío pertenece al comercio | No; 409 atómico | No; 409 atómico |

## Contratos

- Commerce selecciona explícitamente `operadorId` en la consulta; servidor valida que el envío corresponde al comercio de la sesión. Identidad, usuario, comercio y permisos se obtienen de la sesión vigente, nunca del body.
- Transferencia del operador requiere `ComandoId`, `VersionEnvio`, lista no vacía de IDs/versiones de bultos y confirmación explícita de transferencia física. El límite de lote es 100 bultos; no existe límite al tamaño del envío. Cada lote es atómico.
- Acuse requiere `ComandoId`, `VersionTransferencia`, resultado `Conforme|ConObservaciones` y líneas con IDs/versiones exactas. Observaciones de 10–500 caracteres por línea con diferencia. Repetir el comando idéntico devuelve el mismo resultado; mismo ID con actor o contenido distintos da conflicto.
- Consultas: rangos ISO-8601 UTC semiabiertos `[desde,hasta)`, ventanas interactivas de hasta 90 días y paginación server-side estable, máximo 100. Datos privados: `Cache-Control: no-store`.
- API emite sus propios DTOs y ProblemDetails: 400 validación/rango, 401 sesión, 403 permiso/ámbito, 404 ajeno o inexistente bajo respuesta uniforme, 409 estado/versión/custodia/idempotencia, 503 solo indisponibilidad recuperable.

## Reglas de persistencia y seguridad

- PostgreSQL es la fuente canónica. Añadir hechos de transferencia y acuse append-only, relacionados por operador/comercio/envío y por bulto. Registrar comando, huella canónica, acceso/usuario y timestamps UTC.
- RLS habilitada y forzada en cabeceras y líneas; consultas Commerce restringidas por `ComercioId` y `selected_operator_id`, operador por su tenant. El runtime no será dueño y no recibirá bypass RLS. Sin grants directos que permitan fabricar/editar/eliminar hechos; usar las primitivas restringidas que establezca la migración.
- Permiso nuevo `devoluciones.gestionar` aplicable solo a Commerce. Asignar inicialmente al rol de propietario/administrador protegido usando el bootstrap idempotente; roles propios obtienen el permiso por concesión explícita. Volver a comprobar acceso/permiso antes de ejecutar y antes de devolver un replay.
- Guardar la transferencia física con los cambios canónicos y el contexto de historia en una transacción; reusar los límites de bloqueo por operador y orden determinista. No duplicar escritores ni disparar doble outbox/eventos.

## Estados de pantalla

El listado distingue `En retorno`, `En depósito — pendiente de entrega física`, `Pendiente de acuse`, `Recibido conforme`, `Recibido con observaciones` e `Histórico sin evidencia suficiente`. Siempre presenta por separado el estado agregado del envío, resultado/custodia por bulto y hechos acreditados. Selección de operador/filtros cancela cargas anteriores y limpia la selección. El UI conserva entrada/ComandoId ante un resultado de red desconocido y permite reintentar la misma intención.

## Pruebas de cierre

Probar con PostgreSQL/PostGIS y rol runtime real: retorno total y parcial; cancelado/extraviado/retiro; transferencia elegible/ineligible, versiones y selecciones mixtas; permisos revocados y RLS por comercio/operador; acuse conforme/observado/duplicado; comando repetido tras reinicio; rollback, respuesta perdida, concurrencia entre dos conexiones; histórico sin evidencia; relectura post reinicio. UI Commerce debe ser operable con teclado, a 320/1440 CSS px y zoom 200 %, con estados de carga, vacío, error y conflicto.

## Fuentes

- `docs/alcance/Laboratorio__NET_2026.pdf` §§3.3, 4, 5.1, 5.2, 5.4, 6.
- `docs/alcance/Calcs/Casos de Uso - Responsables.xlsm`, `Alcance aplicaciones!A7:G7`, CU-04/11/12/22 y D03/D13.
- `docs/alcance/Entregas/Entrega-10-4-2026-Grupo-4.pdf`, Anexo B, p.12.
- `docs/alcance/ADR-MT-001-PostgreSQL-RLS.md`.
- `docs/cambios-requerimientos/Cambios de Requerimientos Planificados.pdf`, requisitos 31–34 y 37–41.
- Implementación actual y brecha registrada en `docs/auditorias/ap-04/estado-inicial.md`.
