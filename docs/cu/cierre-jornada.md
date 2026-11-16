# Cierre de jornada con incidencias

El repartidor puede terminar la jornada aunque haya bultos sin entrega ni rendición, según D18. El cierre registra incidencias y conserva la custodia física registrada.

La decisión DEC-01 mantiene la prueba pendiente al procesar `FinJornada`. La evidencia se evalúa contra el plazo vigente y el Worker resuelve vencimientos. En los flujos con resultados por bulto, cada entrega con prueba pendiente tiene su propio plazo, calculado desde la recepción de ese hecho. El estado del envío se recalcula con los resultados de sus bultos. [Resultados y plazos por bulto](../adr/ADR-031-041.md)

`FinJornada` se procesa después de todas las operaciones anteriores de su secuencia. Por cada bulto que sigue bajo custodia del repartidor de esa ruta, el servidor conserva en `OperacionesJornada.IncidenciasCierre` su envío, identificador, código y versión, la operación de cierre y los instantes del dispositivo y de recepción. Ruta, operador y acceso responsable pertenecen al mismo registro inmutable. No se acepta una lista de faltantes elegida por el cliente.

El cierre y las incidencias se guardan en la misma transacción. El UUID y hash del comando conservan la respuesta al reintentar; el trigger existente impide editar o borrar el historial. La columna JSON comparte las restricciones RLS del diario. Migración: `20261020224947_RecordClosingIncidents`.

- Entrega física pendiente de prueba. El cierre conserva el resultado pendiente. La evidencia de la operación original puede completarlo si llega antes del vencimiento aplicable, incluso con la jornada cerrada. El barrido de vencimientos opera por separado.
- Bulto con rendición declarada: conserva `PendienteDeposito`, sin incidencia de faltante.
- Bulto sin entrega ni rendición: conserva `Repartidor`, su ruta y acceso custodios. Una carga parcial tampoco se convierte automáticamente en `EnDeposito`.
- La hoja queda finalizada y el seguimiento local se detiene. El plazo persistido no se reabre ni reinicia por el cierre. La jornada cerrada solo admite sincronizar evidencia de la operación física original bajo las comprobaciones existentes; no habilita nuevas cargas, entregas, intentos ni cambios de ruta.

La app advierte cuántos bultos generan incidencia antes de confirmar. Hoja propia y Backoffice exponen el historial; este permanece visible aunque cambie la custodia después. No se agrega una decisión de aceptación o rechazo del despachador.

Si el bulto aparece y vuelve físicamente al depósito, el endpoint de recepción existente puede recibirlo únicamente cuando hay un cierre aplicado de su ruta con una incidencia para ese bulto, acceso y versión de custodia. Sin esa constancia sigue exigiendo rendición previa. La recepción actualiza la custodia y conserva la incidencia; no cambia por sí sola `EnTransito` o `NoEntregado` a `Reprogramado`. Un nuevo intento requiere el comando explícito de reprogramación y una ruta nueva conforme a DEC-04. No deshace una cancelación ni elimina una asignación futura vigente. El nuevo despacho sigue exigiendo todos los bultos físicamente en depósito.

Según DEC-07, al cerrar antes de la salida el envío permanece `AsignadoRuta`, la parada se suspende y la custodia se conserva. La reasignación requiere estado y versiones vigentes, una ruta elegible y los bultos correspondientes en depósito. El cierre registra una incidencia por cada bulto que cumple el criterio de custodia de la jornada. [Escritura de incidencias](../../src/NexoRuta.Infrastructure/Reparto/EfOperacionesRepartoRepository.cs)

Las pruebas del Backend cubren secuencia, repetición, conservación de custodia, recepción posterior, cierre antes de la salida y vencimientos independientes. El cierre diario automático por Worker permanece pendiente. [Pruebas de jornada](../../tests/NexoRuta.IntegrationTests/Reparto/OperacionesRepartoHttpTests.cs) · [Pruebas de plazos por bulto](../../tests/NexoRuta.IntegrationTests/Reparto/OperacionesRepartoHttpTests.PlazosBultos.cs) · [Pendientes del Worker](../adr/consistencia-y-outbox.md#pendientes-de-implementación)

## CU-06 B2: cierre administrativo separado de disponibilidad (01/11/2026)

D18 sigue vigente: `FinJornada` puede cerrar con incidencias y conserva custodia e historial. Libera la **reserva diaria** existente, pero **no** la nueva ocupación física exclusiva de vehículo y repartidor adquirida al despachar. Una operación offline pendiente tampoco libera recursos en el servidor.

En Backoffice, un operador con `envios.gestionar` y `envios.consultar` confirma por separado:
1. Retorno físico de la ocupación exacta (no recibe bultos, no reabre jornada).
2. Disponibilidad y liberación, únicamente con jornada finalizada, retorno confirmado y custodia pendiente resuelta por recepción real o conciliación vigente de extravío. Una parada suspendida o rendición declarada no equivale a descarga. La incidencia histórica permanece y no bloquea tras resolver custodia.

La desactivación administrativa no libera ocupación ni impide cierre, recepción, retorno o liberación. Medianoche y GPS tampoco acreditan retorno. Despachos sin inicio/salida/carga se anulan explícitamente conservando historial y estados comerciales; no se fabrica un retorno. Esa hoja se cancela y el trabajo se reasigna a una nueva identidad, no se recicla una autorización que podría tener comandos offline pendientes.

El despacho futuro deja de estar permitido en B2: solo se adquiere ocupación en la fecha operativa de la hoja (zona horaria de su política). Planificar futuro sigue permitido. Una ocupación ya autorizada no expira a medianoche: iniciar/sincronizar tarde no se rechaza por el mero cambio de fecha; se exige conservar la ocupación sin retorno/liberación. Se mantiene el rechazo de inicio anterior a la fecha operativa.

Transición, contratos y pruebas: `docs/auditorias/cu06/06-implementacion-b2.md` del superproyecto. Las rutas cerradas anteriores a B2 no reciben retornos ficticios ni ocupaciones eternas; disponibilidad inicial requiere inspección administrativa auditable y, si corresponde, recepción física explícita de carga histórica.
