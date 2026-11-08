# Gestión, liquidaciones y cuenta operativa

Fuente integrada y definición funcional: documentos del repositorio principal `docs/gestion/reportes-liquidaciones-cuenta.md` y `docs/adr/ADR-reportes-liquidaciones-cuenta.md`, basados en ambos PDF del taller. El reporte agregado previo fue reemplazado; sus consumidores se actualizaron juntos.

## Capas y contratos

- Domain/Gestion: transiciones de cierre, reintentos y validación de correcciones firmadas.
- Application/Gestion: contratos de lectura/casos de uso; identidad del comercio resuelta desde IUsuarioActual, no desde el cliente.
- Infrastructure/Gestion: agrupación/paginación en PostgreSQL y comandos transaccionales. Los GET del módulo usan una fotografía RepeatableRead.
- Api/Features/Gestion: contratos HTTP propios y controlador. Base `/api/gestion/operadores/{id}`; `reportes`, `reportes/operaciones/{envio}`, `cuenta`, `liquidaciones`, `liquidaciones/{id}`, `confirmar`, `descartar`, `correcciones`.

Consulta requiere `envios.consultar`; gestión, `envios.gestionar` y actor operador. RLS protege además los datos; Commerce solo obtiene sus cuentas por operador y documentos confirmados. No tiene DML directo de liquidaciones. Los comandos definer validan acceso/rol/permiso dentro de la base. Worker y API no reciben privilegios de superusuario ni membresía de los propietarios de funciones.

## Persistencia y cálculo

Migraciones aditivas `ManagementReporting` y `CommerceSettlements`. Las tablas GestionOperaciones/GestionBultos/GestionMotivos son derivadas, con índice por operador/fecha/cortes. GestionPendientes registra generación capturada y aplicada, más la fecha. El bootstrap de hechos anteriores usa una función definer temporal para conservar la visibilidad bajo RLS forzado, incluso con migrador sin superusuario.

ManagementReportingConsumer añade un consumidor Kafka independiente al stream operativo existente. Cada notificación aplicada reconcilia su operación desde hechos persistidos; heartbeat recupera lotes pendientes, limitados y coordinados por SKIP LOCKED. Repeticiones y orden invertido son inocuos. `request_management_rebuild(operador)` y `reconcile_management` son exclusivas del worker y reconstruyen sin tocar cargos. La frescura y los pendientes son visibles.

Compromisos: plazo de modalidad y cobertura capturados al admitir; nunca ETA. Franjas semanales contemplan el día local actual y anterior para ventanas que cruzan medianoche, siguiendo VentanaRuta. Parciales conservan cada autor del diario de entrega física; evidencia posterior no crea visitas. Retiro usa su franja de recolección y muestra la entrega al comercio como resultado distinto. Sin datos suficientes se identifica SinCompromiso.

La vista MovimientosCuenta deriva el primer cargo y diferencias de snapshots TarifasEnvio, con importes originales. CorreccionesCuenta agrega compensaciones trazables de movimientos ya confirmados. Líneas/correcciones conservan numeric(14,2), precisión existente; total de documento admite la suma de hasta 1000 conceptos. Cuenta y documentos son por operador/comercio. No se inventan pagos: confirmar no modifica el saldo operativo.

Liquidaciones Borrador → Confirmada/Descartada. Preparación fija período/corte y conceptos; confirmación bloquea cuenta y envíos, revalida, reclama cada movimiento con PK única y registra transición/actor/fecha. Confirmada es inmutable; se corrige con nuevo movimiento compensatorio. Cargos de cancelación/devolución/extravío o prueba/operación pendiente permanecen visibles y retenidos: la letra no determina una regla económica para cobrarlos definitivamente.

## Pruebas

`GestionCircuitTests`: cantidades, denominadores, parciales/autores, fechas/medianoche, montos, tarifas/reglas posteriores, concurrencia, idempotencia, estabilidad, compensación y aislamiento (dos operadores/dos comercios, uno compartido), permiso de consulta sin gestión, RLS y migración de hechos existentes.

`GestionWorkerTests`: PostgreSQL/Kafka reales y rol worker restringido; eventos repetidos/invertidos y reconstrucción, sin efecto económico. `GestionBrowserTests`: portales del companion exacto, API/Kestrel/PostgreSQL reales; operador filtra/revisa/confirma y comercio consulta. `NEXORUTA_GESTION_E2E_DIR` habilita la prueba y recibe capturas/JSON por ejecución, nunca key rings ni contraseñas. El CI integrado la ejecuta. Las pruebas de retiro existentes verifican además que un fallo de retiro no cuente como intento fallido de entrega. `ManagementBoundaryTests` mantiene independencia y contratos HTTP en API.
