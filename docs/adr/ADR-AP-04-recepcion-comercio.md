# ADR-M02-AP04 — Acuse comercial después de transferencia física

- Estado: aceptada para la implementación solicitada.
- Fecha: 09/11/2026.
- Alcance: gestión AP-04 en Portal Commerce.

## Contexto

AP-04 es obligatoria, pero el documento de alcance deja el detalle funcional a diseñar. El sistema separa resultado comercial por bulto, custodia física y estado agregado del envío. `Devuelto` puede ocurrir mientras la custodia sigue en depósito; por tanto no demuestra recepción del comercio. El flujo operativo existente registra la entrega física, pero no un acuse del comercio.

## Decisión

Commerce consulta devoluciones asociadas a su organización y operador elegido, y registra una sola vez por bulto el acuse conforme o con observaciones después de una transferencia física nueva, trazable e inmutable. La transferencia la registra el operador y es la única operación AP-04 que mueve custodia. El acuse no altera el agregado, custodia, rutas ni economía del envío.

Las operaciones históricas sin transferencia suficientemente atribuible se presentan como no acreditadas y no aceptan acuse. El acuse se crea con UTC del servidor, actor vigente, comando idempotente y referencia exacta a transferencia/líneas. El permiso Commerce `devoluciones.gestionar` es independiente de `envios.gestionar`.

## Consecuencias

- Se añaden hechos auditables append-only y autorización/RLS coherente entre API y SQL runtime.
- El comando de transferencia existente debe aceptar selección explícita y cerrar la brecha `Devuelto/Deposito` solo con procedencia canónica; los clientes de Backoffice se actualizan juntos.
- La propuesta de acuse es decisión del proyecto, no una cita literal del alcance. No introduce RMA, reembolso, prueba de entrega, evidencia privada ni transición de devolución iniciada por Commerce.
- La migración es aditiva y no backfillea la historia antigua. GCP comparte la autoridad PostgreSQL y se valida únicamente con autorización del destino.
