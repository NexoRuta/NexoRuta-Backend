# Cancelación de envíos

- **Estado:** implementada en API, Comercio, Backoffice y Tracking; verificación controlada, sin aceptación remota en el S21.
- **Módulos:** M02 controla la transición; M03 suspende la parada; M04 conserva la ejecución offline; M05/Worker envía el aviso.
- **Fuente:** plan mobile T11, D04 y D14; aclaraciones del usuario del 19/10/2026. Complementa los CU existentes, sin asignar una numeración nueva.

## Actores y resultado

El comercio cancela sus propios envíos con su permiso vigente `envios.crear`. En el operador solo puede hacerlo el administrador con rol de sistema protegido `administrador_operador` y permiso `envios.gestionar`. El despachador, aunque pueda gestionar/despachar rutas, no puede cancelar. La cuenta, organización y usuario se obtienen de la identidad central.

La cancelación suspende la entrega y conserva la custodia física. Un paquete cargado debe declararse en rendición y ser recibido por depósito. Se avisa únicamente al repartidor actual; no al de una ruta futura. Si estaba en depósito, cancelarlo no inventa una salida ni una recepción.

## Entrada y contrato

`POST /api/envios/{envioId}/cancelacion`, autenticado:

```json
{ "version": 8, "motivo": "Solicitud del comercio." }
```

`version` es obligatorio, entero no negativo; `motivo` es obligatorio y tiene hasta 500 caracteres. Se recortan espacios exteriores. No se aceptan campos adicionales para elegir actor, operador, comercio o destinatario del push.

Respuesta `200`:

```json
{ "cancelacionId": "0199a100-0000-7000-8000-000000000001", "envioId": "0199a100-0000-7000-8000-000000000002", "version": 9, "estado": "Cancelado" }
```

Otros resultados: `400` entrada inválida, `401` sin sesión, `403` actor/permiso no autorizado, `404` envío inexistente o ajeno a la cuenta, `409` versión/estado incompatibles. El contrato generado está en el snapshot OpenAPI del frontend.

## Reglas y concurrencia

- Se permite cancelar desde `Admitido`, `EnDeposito`, `AsignadoRuta`, `EnTransito`, `NoEntregado`, `Reprogramado` y `EnDevolucion`. Se rechaza desde `Entregado`, `Devuelto`, `Extraviado`, `EntregaPendienteValidacion` y `EntregaSinValidacion`. La regla de estado no sustituye la autorización del actor ni la validación del código público.
- Una cancelación nueva requiere que no exista un hecho físico de entrega y que coincida la versión. Si una conciliación offline descubre la entrega después de cancelarse, persiste fecha, prueba y custodia, pero mantiene `Estado = Cancelado`; ese hecho no reactiva ni reemplaza la cancelación.
- La carrera cancelación/entrega se resuelve contra el estado y versión persistidos: si entrega se confirma primero, la cancelación se rechaza; si cancelación se confirma primero, una entrega física tardía solo registra el hecho manteniendo `Cancelado`. Para la carrera con recepción de devolución, la transición a `Devuelto` confirmada primero hace que cancelar falle; si cancela primero, una recepción posterior no cambia el estado terminal.
- Estado, versión, suspensión de paradas, versiones de las rutas afectadas, auditoría y aviso/outbox se guardan en una transacción. El SQL vuelve a comprobar acceso, permiso, actor, versión y estado bajo bloqueo; la base impide cualquier cambio posterior desde `Cancelado` a otro estado.
- Repetir una cancelación autenticada autorizada con el mismo acceso, versión base y motivo devuelve el hecho original sin duplicar auditoría ni outbox, incluso si la conciliación tardía incrementó la versión actual. Se comprueba autorización antes de devolverlo. Otro actor, motivo o referencia de versión produce conflicto; no se modifica la auditoría.
- Repetir una cancelación pública con el código consumido válido reconoce el resultado aunque luego se registre un hecho físico tardío. Un código incorrecto no se considera idempotencia ni autoriza resultado exitoso.

`CancelacionesEnvio` conserva actor, organización, usuario, estado/versión anteriores, motivo, versión resultante y fecha del servidor. Tiene FK al envío del operador y al acceso de la organización del actor, índice único por envío, RLS forzado y trigger que impide actualizar o borrar el hecho.

La API mantiene sus políticas actuales: el comercio no obtiene UPDATE general de envíos ni lectura de rutas ajenas. La función acotada `nexoruta_app.cancel_shipment` tiene propietario NOLOGIN/NOBYPASSRLS, búsqueda fija, políticas limitadas al envío de la cuenta y permisos de columnas. Verifica otra vez actor, rol y permiso actuales. Serializa con reparto/reasignación mediante el mismo bloqueo del operador. El Worker reutiliza su contrato y funciones existentes, sin acceso general a las tablas operativas.

La migración `20261024120000_EnforceCancellationTerminalState` centraliza la matriz SQL en `nexoruta_app.cancel_state_is_allowed`, la concede solo a las funciones privadas existentes, añade `tr_envios_cancelado_terminal` como guardia de persistencia y elimina `CK_Envios_Cancelacion`, que impedía conservar un hecho físico tardío junto con el estado terminal. No otorga DML general al rol API. Su rollback vuelve a la matriz previa de seis estados y bloquea la reversión si ya existen hechos físicos conciliados en envíos cancelados.

Migraciones anteriores: `20261019225246_AddRecipientEmail`, `20261019225313_RequireRecipientEmail` y `20261019230046_AddPublicShipmentCancellation`. Los fixtures de integración aplican las migraciones a bases efímeras; no se aplicaron ni modificaron bases compartidas o de producción.

## Interfaces y verificación

Comercio y Backoffice exigen elegir la acción, indicar motivo y confirmar. Backoffice usa antiforgery y comprueba rol/permiso antes de invocar la API. Comercio permite actualizar la lista, conserva el motivo ante fallo y trata la sesión vencida mediante el ingreso central. La API sigue decidiendo sobre estado y autorización aunque se manipule el formulario.

Las regresiones cubren actores autorizados, despachador denegado, otra cuenta por HTTP y SQL restringido, versiones obsoletas, repeticiones concurrentes, entrega concurrente, retorno sin entrega, custodia conservada, aviso solo al custodio original, inmutabilidad y rollback. La evidencia y cantidades ejecutadas se registran en [ejecución del plan móvil](../../../mobile/docs/ejecucion-completa.md).

## Cancelación pública desde Tracking

El comercio registra **correo obligatorio del destinatario en cada envío**, validado en el formulario, API y dominio. No se incorpora ese contacto a respuestas de seguimiento ni al listado público. El enlace de lectura por sí solo no autoriza cancelar.

1. `POST /api/public/seguimiento/{token}/cancelacion/codigo`: sin sesión ni correo en el cuerpo. El servidor deriva envío y correo registrados de un enlace vigente y encola un código aleatorio de doce caracteres. `202` significa encolado, no correo entregado. No devuelve el código, correo ni identificadores internos.
2. `POST /api/public/seguimiento/{token}/cancelacion` con `{ "codigo": "ABCD-EFGH-JKMN" }`: valida el código de ese envío y cancela bajo el mismo bloqueo/transacción que la cancelación autenticada. `200` devuelve `{ "estado": "Cancelado" }`. No admite actor, motivo ni versión elegidos por el visitante.
3. Código sin vencimiento temporal, de un uso, cinco intentos confirmados también ante `400`. Reemitir desactiva el anterior; mínimo sesenta segundos entre solicitudes por envío, además del límite público por IP. `429` informa espera; `400` código inválido/agotado; `404` enlace no vigente o envío que ya no permite cancelar. El vencimiento/revocación del **enlace de seguimiento** sigue vigente por separado. Repetir el mismo código consumido confirma el resultado mientras siga cancelado; no ejecuta otra transición.

El corte se basa en el hecho físico, incluso con prueba incompleta. Una entrega offline concurrente sigue la conciliación D04 y conserva el hecho recibido. Si el bulto no se entregó, la cancelación conserva su custodia para rendición/recepción. No interviene el despachador.

`CodigosCancelacionPublica` conserva hash bcrypt con costo 12, intentos, uso y estado del correo; tiene RLS forzado y ningún acceso directo de API/Worker. La auditoría pública referencia esa autorización sin inventar usuario o acceso. Un núcleo privado comparte los cambios atómicos de cancelación; la API no puede ejecutarlo directamente. Funciones separadas para API y Worker verifican el ámbito y el estado actual.

El outbox `CodigoCancelacionEmail` reutiliza RabbitMQ y el SMTP del Worker. Su sobre está protegido con Data Protection; el broker transporta solo el ID del mensaje. El consumidor obtiene el sobre autorizado de PostgreSQL, omite códigos reemplazados/usados/enviados y envíos entregados, y registra el envío después de SMTP. API y Worker deben compartir el anillo de claves y `ApplicationName` existentes; no se registra el código ni el contacto. Entrega al menos una vez: una caída entre SMTP y el acuse puede repetir el mismo correo/código, sin duplicar cancelaciones. Tras diez reentregas fallidas el mensaje va a `nexoruta.cancelacion.codigo.email.dead.v1`; su revisión operativa sigue siendo necesaria. No se presenta publicación en RabbitMQ como éxito SMTP.

Migraciones nuevas: `20261019225246_AddRecipientEmail`, `20261019225313_RequireRecipientEmail` y `20261019230046_AddPublicShipmentCancellation`. Con datos previos se debe detener en la primera y completar correos reales antes de la segunda: no hay valores ficticios ni adaptadores para el contrato anterior. La reversión de la última se bloquea si eliminaría autorizaciones asociadas a cancelaciones públicas. No se aplicaron estas migraciones a la base local.

## Verificación de la regla terminal

La matriz autenticada y pública, repetición tras conciliación tardía, error de código, rollback, RLS, actualización directa de estado y carreras con entrega/devolución se verifican en PostgreSQL efímero. La evidencia y los comandos del bloque del 24/10/2026 están en [auditoría del bloque de cancelación terminal](../../../docs/auditorias/cu07/07-bloque-cancelacion-terminal.md). Este bloque no implementa importación ni cierra el resto de la máquina de estados.
## Pendientes
Falta SMTP real configurado, aceptación de push remoto con API HTTPS y recorrido completo en S21. Las pruebas usan PostgreSQL y RabbitMQ reales aislados, más un remitente de correo controlado; no acreditan entrega a un buzón real. iOS no está compilado ni probado. Este corte no implementa una recogida posterior a entrega ni modifica sus reglas por inferencia.
