# Cierre de jornada con incidencias

Decisión D18 confirmada el 20/10/2026: el repartidor puede terminar la jornada aunque haya bultos sin entrega ni rendición. No se inventan entregas, pérdidas ni recepciones en depósito.

`FinJornada` se procesa después de todas las operaciones anteriores de su secuencia. Por cada bulto que sigue bajo custodia del repartidor de esa ruta, el servidor conserva en `OperacionesJornada.IncidenciasCierre` su envío, identificador, código y versión, la operación de cierre y los instantes del dispositivo y de recepción. Ruta, operador y acceso responsable pertenecen al mismo registro inmutable. No se acepta una lista de faltantes elegida por el cliente.

El cierre y las incidencias se guardan en la misma transacción. El UUID y hash del comando conservan la respuesta al reintentar; el trigger existente impide editar o borrar el historial. La columna JSON comparte las restricciones RLS del diario. Migración: `20261020224947_RecordClosingIncidents`.

- Entrega física pendiente de prueba: pasa a `EntregaSinValidacion` al cerrar; no se pide rendir un bulto que recibió el destinatario.
- Bulto con rendición declarada: conserva `PendienteDeposito`, sin incidencia de faltante.
- Bulto sin entrega ni rendición: conserva `Repartidor`, su ruta y acceso custodios. Una carga parcial tampoco se convierte automáticamente en `EnDeposito`.
- La hoja queda finalizada y el seguimiento local se detiene. El plazo de prueba no se reabre y no se admiten nuevas entregas de esa jornada.

La app advierte cuántos bultos generan incidencia antes de confirmar. Hoja propia y Backoffice exponen el historial; este permanece visible aunque cambie la custodia después. No se agrega una decisión de aceptación o rechazo del despachador.

Si el bulto aparece y vuelve físicamente al depósito, el endpoint de recepción existente puede recibirlo únicamente cuando hay un cierre aplicado de su ruta con una incidencia para ese bulto, acceso y versión de custodia. Sin esa constancia sigue exigiendo rendición previa. La recepción actualiza la custodia, conserva la incidencia y deja los envíos en tránsito/no entregados como `Reprogramado`. No deshace una cancelación ni elimina una asignación futura vigente. El nuevo despacho sigue exigiendo todos los bultos físicamente en depósito.

Pruebas HTTP incluyen rol PostgreSQL restringido, secuencia fuera de orden, repetición idéntica, inmutabilidad SQL, conservación de custodia, carga sin salida, recepción posterior y rechazo de entrega tras cerrar. Las pruebas móviles verifican persistencia de incidencias propias y rechazo de referencias a bultos ajenos. El recorrido físico con faltantes en el S21 continúa pendiente de aceptación operativa.
