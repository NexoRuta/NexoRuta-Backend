# Destino confirmado para la hoja móvil

M02 conserva la dirección y su confirmación; M03 solo planifica con destinos ya confirmados. Corresponde al alta CU-07 y a la dependencia de CU-18/AP-08 del plan móvil. El usuario confirmó Geoapify Free detrás de interfaz el 20/10/2026.

## Flujo

El comercio crea el envío con dirección y correo del destinatario. Desde el alta o «Mis envíos» abre «Ubicación del destino». Se consultan automáticamente hasta cinco propuestas, sin elegir ni guardar una cuando hay varias. El comercio selecciona una, revisa el mapa, corrige el punto si hace falta y confirma expresamente. Ni el geocodificador ni la carga del mapa confirman el destino.

Si no hay coincidencias, falla el proveedor, se alcanza su límite o falta la clave, no se inventa `(0,0)`. El formulario conserva la dirección y permite indicar coordenadas manuales. El mapa avisa cuando falta configuración; no simula una selección visual. Cambiar de envío limpia el formulario y cancela consultas anteriores. Cambiar el punto desmarca la confirmación.

## API vigente

Todas las operaciones requieren Bearer de comercio y permiso vigente `envios.crear`. El servidor deriva comercio y usuario de la identidad; otro comercio recibe 404 y un operador, 403.

- `GET /api/envios/{id}/destino`: dirección, versión **de la dirección**, estado, coordenadas y fecha de confirmación. Coordenadas/fecha nulas significan sin confirmar.
- `POST /api/envios/{id}/destino/propuestas`: sin dirección elegida por el visitante; busca la dirección almacenada del envío propio. Devuelve dirección/versión consultadas, atribución y propuestas. 200 con lista vacía significa sin coincidencias; 503 significa proveedor indisponible; 429 limita consultas. No escribe ubicaciones.
- `PUT /api/envios/{id}/destino`: `{ "version": 0, "latitud": -34.9, "longitud": -56.2 }`. Los tres campos son obligatorios; no acepta campos extra. 204 confirma, 400 entrada inválida, 404 fuera de cuenta y 409 versión/estado cambiados.

Solo se confirma antes de recepción en depósito (`Admitido`). El dominio verifica coordenadas finitas y rangos. La función SQL `confirm_shipment_destination` revalida acceso/permiso, pertenencia, versión y estado bajo el mismo bloqueo del operador que recepción/planificación. Su propietario es NOLOGIN/NOBYPASSRLS y dispone únicamente de las columnas necesarias. La API no obtiene acceso general a rutas u operadores para confirmar direcciones. Dos confirmaciones de una misma versión no se pisan: una confirma y la otra recibe 409.

La responsabilidad se retiró de `GestionarRutasUseCase`/`IRutasRepository` y quedó en `GestionarDestinoEnvioUseCase`/`IDestinosEnvioRepository`, sin cambiar el contrato HTTP de confirmación ni introducir alias.

## Integraciones y configuración

- Backend: `Geocoding__Geoapify__ApiKey`, fuera de Git y solo en servidor. Cliente externo de ocho segundos, cinco resultados, respuesta limitada a 64 KiB y caché en memoria de 24 horas. No reintenta automáticamente ni convierte errores en resultados vacíos. Sin clave devuelve 503; no usa un proveedor alternativo por inferencia.
- Commerce: `Mapbox__PublicAccessToken`, solo token `pk.`. El selector usa Mapbox GL JS 3.33.0, igual que Backoffice, con atribución y sin navegación de giro. La clave Geoapify nunca llega al navegador. El mapa no genera coordenadas de destino por defecto.
- Se suprimen los logs automáticos del cliente HTTP Geoapify y sus trazas HTTP exportadas, para que la clave y la dirección de consulta no se registren. Los errores públicos son genéricos y no conservan la excepción externa.
- Se conservan atribuciones de Geoapify y OpenStreetMap en los resultados. El límite gratuito y sus condiciones deben revalidarse al configurar el servicio; no se contrata un plan pago automáticamente.

[API y límites Geoapify](https://apidocs.geoapify.com/docs/geocoding/), [plan gratuito y atribución](https://www.geoapify.com/pricing/). No se usa el Nominatim público como backend de seguimiento de paquetes: [política del servicio](https://operations.osmfoundation.org/policies/nominatim/).

## Evidencia

Las pruebas cubren roles SQL restringidos, cuenta ajena, operador denegado, propuesta sin escritura, confirmación concurrente, recepción, versiones y JSON incompleto; el adaptador se prueba con respuestas controladas, caché, cuota/fallo, datos inválidos, exceso de tamaño y cancelación. La página real se renderiza y se invocan sus controles de selección/confirmación, conservando versión y coordenadas del PUT.

La migración `20261020222152_AuthorizeShipmentDestinationConfirmation` está preparada; no se aplicó a una base externa. No se acreditan búsquedas Geoapify reales ni mapa real sin sus claves. El usuario confirmó que no dispone de servicios y autorizó preparar entorno local; esas dependencias se registran por separado.
