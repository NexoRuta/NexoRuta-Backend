# Cierre de jornada con incidencias

Decisión D18 confirmada el 20/10/2026: el repartidor puede terminar la jornada aunque haya bultos sin entrega ni rendición. No se inventan entregas, pérdidas ni recepciones en depósito.

**Aclaración vigente DEC-01 (25/10/2026, opción B):** se reemplaza únicamente la regla anterior que hacía que `FinJornada` moviera `EntregaPendienteValidacion` a `EntregaSinValidacion`. El cierre deja el envío pendiente; una prueba completa recibida antes del vencimiento o el Worker al alcanzar el vencimiento son los únicos disparadores de esas transiciones. La regla no modifica las demás condiciones de cierre, custodia e incidencias de D18.

`FinJornada` se procesa después de todas las operaciones anteriores de su secuencia. Por cada bulto que sigue bajo custodia del repartidor de esa ruta, el servidor conserva en `OperacionesJornada.IncidenciasCierre` su envío, identificador, código y versión, la operación de cierre y los instantes del dispositivo y de recepción. Ruta, operador y acceso responsable pertenecen al mismo registro inmutable. No se acepta una lista de faltantes elegida por el cliente.

El cierre y las incidencias se guardan en la misma transacción. El UUID y hash del comando conservan la respuesta al reintentar; el trigger existente impide editar o borrar el historial. La columna JSON comparte las restricciones RLS del diario. Migración: `20261020224947_RecordClosingIncidents`.

- Entrega física pendiente de prueba: conserva `EntregaPendienteValidacion` al cerrar; no se pide rendir un bulto que recibió el destinatario. La regla y fecha de vencimiento quedan fijadas desde la primera recepción válida del servidor para la versión del envío. La evidencia recibida antes del instante de vencimiento puede completar la prueba aunque la jornada haya cerrado; en el instante límite o después, no valida el envío automáticamente.
- Bulto con rendición declarada: conserva `PendienteDeposito`, sin incidencia de faltante.
- Bulto sin entrega ni rendición: conserva `Repartidor`, su ruta y acceso custodios. Una carga parcial tampoco se convierte automáticamente en `EnDeposito`.
- La hoja queda finalizada y el seguimiento local se detiene. El plazo persistido no se reabre ni reinicia por el cierre. La jornada cerrada solo admite sincronizar evidencia de la operación física original bajo las comprobaciones existentes; no habilita nuevas cargas, entregas, intentos ni cambios de ruta.

La app advierte cuántos bultos generan incidencia antes de confirmar. Hoja propia y Backoffice exponen el historial; este permanece visible aunque cambie la custodia después. No se agrega una decisión de aceptación o rechazo del despachador.

Si el bulto aparece y vuelve físicamente al depósito, el endpoint de recepción existente puede recibirlo únicamente cuando hay un cierre aplicado de su ruta con una incidencia para ese bulto, acceso y versión de custodia. Sin esa constancia sigue exigiendo rendición previa. La recepción actualiza la custodia y conserva la incidencia; no cambia por sí sola `EnTransito` o `NoEntregado` a `Reprogramado`. Un nuevo intento requiere el comando explícito de reprogramación y una ruta nueva conforme a DEC-04. No deshace una cancelación ni elimina una asignación futura vigente. El nuevo despacho sigue exigiendo todos los bultos físicamente en depósito.

**Alineación DEC-07 (28/10/2026):** se reemplaza la frase anterior que atribuía a la recepción un cambio automático a `Reprogramado`. Al cerrar una jornada antes de la salida, el envío permanece `AsignadoRuta`, la parada se suspende y la custodia no cambia; una reasignación posterior solo se permite con estado/versión vigentes, ruta elegible y todos los bultos realmente en depósito. La rendición, la recepción y el cierre registran hechos/custodia, no inventan una flecha comercial. El comportamiento está limitado al bulto único y no define agregación CU-08.

Pruebas HTTP incluyen rol PostgreSQL restringido, secuencia fuera de orden, repetición idéntica, inmutabilidad SQL, conservación de custodia, carga sin salida, recepción posterior y rechazo de entrega tras cerrar. Las pruebas móviles verifican persistencia de incidencias propias y rechazo de referencias a bultos ajenos. El recorrido físico con faltantes en el S21 continúa pendiente de aceptación operativa.
