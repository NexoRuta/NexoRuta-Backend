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

- Se permite desde `Admitido`, `EnDeposito`, `AsignadoRuta`, `EnTransito`, `NoEntregado` y `Reprogramado`, sin entrega física registrada. El dominio valida la transición; la escritura SQL vuelve a comprobar versión y precondiciones bajo bloqueo.
- Después de una entrega física, incluso con prueba pendiente o sin validación, corresponde devolución. La cancelación no deshace ese hecho.
- Si se cancela mientras el repartidor registra offline una entrega recibida, la conciliación automática comprueba jornada, carga y custodia originales y conserva la entrega. Su prueba se valida según la política de esa jornada; no interviene despacho.
- Si no se entregó, rendición y recepción conservan `Cancelado`, sin fecha de entrega física.
- Estado, versión, suspensión de paradas, versiones de las rutas afectadas, auditoría y aviso/outbox se guardan en una transacción. Se incrementa también la ruta del custodio actual aunque su parada ya estuviera suspendida por reasignación, para no reutilizar una versión de vista remota con información anterior.
- Dos cancelaciones iguales concurrentes generan un único registro y aviso. Repetir acceso, versión base y motivo devuelve la misma respuesta mientras el envío siga cancelado en esa versión. Una modificación posterior, otro motivo o actor producen conflicto; no se modifica la auditoría original.

## Seguridad y persistencia

`CancelacionesEnvio` conserva actor, organización, usuario, estado/versión anteriores, motivo, versión resultante y fecha del servidor. Tiene FK al envío del operador y al acceso de la organización del actor, índice único por envío, RLS forzado y trigger que impide actualizar o borrar el hecho.

La API mantiene sus políticas actuales: el comercio no obtiene UPDATE general de envíos ni lectura de rutas ajenas. La función acotada `nexoruta_app.cancel_shipment` tiene propietario NOLOGIN/NOBYPASSRLS, búsqueda fija, políticas limitadas al envío de la cuenta y permisos de columnas. Verifica otra vez actor, rol y permiso actuales. Serializa con reparto/reasignación mediante el mismo bloqueo del operador. El Worker reutiliza su contrato y funciones existentes, sin acceso general a las tablas operativas.

Migración: `20261019223054_AddAuthenticatedShipmentCancellation`. No se aplicó a la base local. Su reversión se comprueba en base temporal y retira función, políticas y permisos de columnas agregados.

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

## Pendientes

Falta SMTP real configurado, aceptación de push remoto con API HTTPS y recorrido completo en S21. Las pruebas usan PostgreSQL y RabbitMQ reales aislados, más un remitente de correo controlado; no acreditan entrega a un buzón real. iOS no está compilado ni probado. Este corte no implementa una recogida posterior a entrega ni modifica sus reglas por inferencia.
