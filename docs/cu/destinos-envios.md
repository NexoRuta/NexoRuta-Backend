# Destino confirmado para la hoja móvil

> Selector vigente: Mapbox temporal (`permanent=false`), selección, ajuste opcional, confirmación y guardado de dirección/coordenadas autorizados expresamente por el usuario. Se eliminó el modo vista previa y sus bloqueos. [Implementación y verificación](../../../docs/auditorias/geocodificacion/03-selector-guardar-reabrir.md). El adaptador vial Geoapify B4/B5 no cambia.

M02 conserva la dirección y su confirmación; M03 solo planifica con destinos ya confirmados. Corresponde al alta CU-07 y a la dependencia de CU-18/AP-08 del plan móvil. El usuario confirmó Geoapify Free detrás de interfaz el 20/10/2026.

## Flujo

El comercio crea el envío con dirección y correo del destinatario. Desde el alta o «Mis envíos» abre «Ubicación del destino». La búsqueda explícita al escribir al menos tres caracteres consulta sugerencias con debounce y cancelación; renderizar o reabrir no busca. No se elige ni guarda una automáticamente. El comercio selecciona una, revisa el mapa, corrige el punto si hace falta y confirma expresamente. Ni el geocodificador ni la carga del mapa confirman el destino.

Si no hay coincidencias, falla el proveedor, se alcanza su límite o falta la clave, no se inventa `(0,0)`. El formulario conserva la dirección y permite indicar coordenadas manuales. El mapa avisa cuando falta configuración; no simula una selección visual. Cambiar de envío limpia el formulario y cancela consultas anteriores. Cambiar el punto desmarca la confirmación.

## API vigente

Todas las operaciones requieren Bearer de comercio y permiso vigente `envios.crear`. El servidor deriva comercio y usuario de la identidad; otro comercio recibe 404 y un operador, 403.

- `GET /api/envios/{id}/destino`: dirección, versión **de la dirección**, estado, coordenadas y fecha de confirmación. Coordenadas/fecha nulas significan sin confirmar.
- `POST /api/envios/{id}/destino/propuestas`: sin dirección elegida por el visitante; busca la dirección almacenada del envío propio. Devuelve dirección/versión consultadas, atribución y propuestas. 200 con lista vacía significa sin coincidencias; 503 significa proveedor indisponible; 429 limita consultas. No escribe ubicaciones.
- `PUT /api/envios/{id}/destino`: `{ "version": 0, "latitud": -34.9, "longitud": -56.2, "direccion": "Dirección seleccionada" }`. Versión y coordenadas son obligatorias; `direccion` opcional conserva el texto actual cuando es null (selección manual). Si se envía, debe tener 1–240 caracteres no vacíos. No acepta campos extra. Texto y coordenadas se guardan atómicamente con un único incremento de versión; no se almacena la respuesta completa del proveedor. 204 confirma, 400 entrada inválida, 404 fuera de cuenta y 409 versión/estado cambiados.

La confirmación inicial se admite mientras el envío está `Admitido` o, si ya fue recibido, mientras sigue `EnDeposito` y el destino aún está pendiente. La recepción no lo confirma automáticamente; la planificación sigue exigiendo coordenadas y confirmación expresa. Si el destino ya estaba confirmado, todavía puede corregirse mientras el envío permanezca `Admitido`, como antes; después de la recepción no se puede cambiar. Los estados posteriores —incluido `AsignadoRuta`— tampoco admiten cambios. El PDF original (§5.2 req. 10 y §5.3 reqs. 13–17) y la planilla (`Dependencias!G7:H8`) no exigen confirmar antes de recibir; el requisito operativo conservado es confirmar antes de planificar.

El dominio verifica coordenadas finitas y rangos. La función SQL `confirm_shipment_destination` revalida acceso/permiso, pertenencia, versión y estado bajo el mismo bloqueo del operador que recepción/planificación; tras recibir, además exige que el destino siga pendiente al tomar el bloqueo. Su propietario es NOLOGIN/NOBYPASSRLS y dispone únicamente de las columnas necesarias. La API no obtiene acceso general a rutas u operadores para confirmar direcciones. Dos confirmaciones de una misma versión no se pisan: una confirma y la otra recibe 409.

La responsabilidad se retiró de `GestionarRutasUseCase`/`IRutasRepository` y quedó en `GestionarDestinoEnvioUseCase`/`IDestinosEnvioRepository`, sin cambiar el contrato HTTP de confirmación ni introducir alias.

## Integraciones y configuración

- Backend: `Geocoding__Geoapify__ApiKey`, fuera de Git y solo en servidor. Las llamadas reales están **deshabilitadas por defecto**, incluso si existe una clave. Solo habilitar `Geocoding__Geoapify__PermitirLlamadasReales=true` después de verificar la cuenta, el presupuesto compartido y las condiciones indicadas abajo. Cliente externo de ocho segundos (configurable), cinco resultados, respuesta limitada a 64 KiB y caché en memoria de 24 horas. No reintenta automáticamente ni convierte errores en resultados vacíos. Sin habilitación, clave o contador disponible devuelve 503; no usa un proveedor alternativo.
- Commerce: `Mapbox__PublicAccessToken`, solo token `pk.`. El selector usa Mapbox GL JS 3.33.0, igual que Backoffice, con atribución y sin navegación de giro. La clave Geoapify nunca llega al navegador. El mapa no genera coordenadas de destino por defecto.
- Se suprimen los logs automáticos del cliente HTTP Geoapify y sus trazas HTTP exportadas, para que la clave y la dirección de consulta no se registren. Los errores públicos son genéricos y no conservan la excepción externa.
- Se conservan atribuciones de Geoapify y OpenStreetMap en los resultados. El límite gratuito y sus condiciones deben revalidarse al configurar el servicio; no se contrata un plan pago automáticamente.

[API y límites Geoapify](https://apidocs.geoapify.com/docs/geocoding/), [plan gratuito y atribución](https://www.geoapify.com/pricing/). No se usa el Nominatim público como backend de seguimiento de paquetes: [política del servicio](https://operations.osmfoundation.org/policies/nominatim/).

### Corrección de consumo permanente (01/11/2026)

Commerce ya no geocodifica con Mapbox. `SelectorDestino.razor` usa `DestinosEnvioApiClient.BuscarAsync` → `POST /api/envios/{id}/destino/busqueda`, cuerpo `{"consulta":"Calle de prueba"}` → `GestionarDestinoEnvioUseCase` → el mismo `IGeocodificador`/`GeocodificadorGeoapify`. Este camino usa `/v1/geocode/autocomplete`; las propuestas de dirección completa y de base operativa mantienen `/v1/geocode/search`. No hay llamadas de detalle al seleccionar ni de geocodificación al confirmar. `destino-mapbox.js` queda exclusivamente para mapa, marcador y teclado.

El endpoint exige las mismas políticas `Comercio` y `envios.crear`, verifica pertenencia/estado del envío y no escribe. Acepta consultas de 3–240 caracteres tras trim; JSON inválido devuelve 400, envío ajeno 404, estado bloqueado 409. Respuesta 200: el mismo `PropuestasDestinoDatos` (dirección registrada, versión, atribución y propuestas con descripción/latitud/longitud). La selección es provisional; GET/PUT y la persistencia de coordenadas/versiones no cambian. **La sugerencia no reemplaza la dirección registrada**. Se conservan debounce 300 ms, cancelación y descarte por secuencia; mapa manual y búsqueda son independientes de la disponibilidad del otro.

| Configuración servidor (`Geocoding:Geoapify`) | Predeterminado | Restricción |
|---|---:|---|
| `PermitirLlamadasReales` | `false` | Clave sola no habilita consumo |
| `ApiKey` | sin valor en Git | Solo secretos/entorno del backend |
| `MaxSolicitudes24Horas` | 500 | 1–2000; ventana móvil de 24 horas, global |
| `SolicitudesPorSegundo` | 3 | 1–3; ventana móvil global de un segundo |
| `SolicitudesPorMinutoUsuario` | 10 | 1–60; política HTTP existente |
| `TimeoutSegundos` | 8 | 1–30; incluye reserva/lectura del proveedor |

Las variables .NET usan `__` en lugar de `:`. No basta agregar variables al `.env` raíz para pasarlas a contenedores: deben inyectarse explícitamente en el entorno del backend mediante la configuración de ejecución/secretos, sin incluirlas en el frontend. Este cambio no modifica despliegues ni activa ese flag.

La migración **`20261102000000_LimitGeocodingConsumption`** agrega únicamente contabilidad de solicitudes (sin direcciones, contactos ni claves), compartida entre procesos y persistente tras reinicios. No modifica destinos ni borra migraciones/datos de negocio. La reserva es atómica con bloqueo global, anterior al HTTP y no se devuelve por cancelación/fallo: una solicitud podría haber sido cobrada aun sin respuesta. La caché no consume nuevas reservas. Si falta la migración o falla PostgreSQL, se bloquea la salida (503), sin alternativa paga. Ya se aplicó a la base local de desarrollo, después de B4 y con respaldo cifrado previo; los siete casos PostgreSQL y el circuito completo con proveedor controlado aprobaron. Véase el [resultado definitivo](../../../docs/auditorias/geocodificacion/resultado.md). Las llamadas reales siguen deshabilitadas.

Los límites locales devuelven 429, `Retry-After` y `code=geocoding_quota_exhausted` o `geocoding_rate_limited`. HTTP 402/429 del proveedor se trata conservadoramente como cuota agotada y bloquea nuevas consultas de ese proceso por 24 horas; ese circuito está en memoria y se reinicia con el proceso, **no** el contador persistente. Fallos, JSON inválido y timeout devuelven 503 genérico. No hay reintentos ni redirecciones automáticas ni fallback. El consumidor de base operativa también transforma la excepción de límite en 429.

### Condiciones oficiales y límite de la garantía

Verificadas el 01/11/2026 mediante documentación pública, sin API ni cuenta:

- [Autocomplete: almacenamiento y caché permitidos](https://www.geoapify.com/address-autocomplete/), sección *Affordable and Permissive*. [Geocoding: direcciones y coordenadas almacenables](https://www.geoapify.com/geocoding-api/). Esto permite conservar el punto del destino; no se sustituye `permanent=true` por un modo temporal de Mapbox.
- [Plan Free](https://www.geoapify.com/pricing/): 3.000 créditos/día, hasta 5 solicitudes/s y sin tarjeta requerida. [Documentación autocomplete](https://apidocs.geoapify.com/docs/geocoding/address-autocomplete/): una solicitud equivale a un crédito; los créditos se acumulan entre APIs de la cuenta.
- **No es un corte de facturación**: Pricing declara límites *soft* y los [términos, Plans and usage limits](https://www.geoapify.com/terms-and-conditions/) reservan suspensión o cargos por exceso. La cuota gratuita no demuestra costo cero ni bloqueo efectivo de sobrecargos.
- [Términos, Attribution](https://www.geoapify.com/terms-and-conditions/): atribución OpenStreetMap siempre y Geoapify en Free. El selector conserva enlaces visibles `Powered by Geoapify` (sin `nofollow`) y `© OpenStreetMap contributors`, también al mostrar un destino ya confirmado.

**Antes de habilitar**: revisar en la cuenta el plan Free efectivo, facturación/sobrecargos y cualquier bloqueo que el proveedor efectivamente ofrezca; contabilizar todos los proyectos y APIs que compartan cuota. El cupo local cubre solo este geocodificador (destinos y base operativa), no `RoutingGeoapify` ni otras aplicaciones. Incluso 500 locales no prueban que el total de la cuenta permanezca en Free. Si no se puede acreditar la configuración de la cuenta, mantener el flag apagado y usar selección manual. El mapa Mapbox conserva su propia facturación potencial, independiente de geocodificación.

El código no revoca tokens, no cambia facturación ni bloquea aplicaciones externas o versiones anteriores que sigan usando las mismas credenciales.

## Evidencia

Las pruebas cubren roles SQL restringidos, cuenta ajena, operador denegado, propuesta sin escritura, confirmación concurrente, recepción, versiones y JSON incompleto; el adaptador se prueba con respuestas controladas, caché, cuota/fallo, datos inválidos, exceso de tamaño y cancelación. La página real se renderiza y se invocan sus controles de selección/confirmación, conservando versión y coordenadas del PUT.

La migración `20261020222152_AuthorizeShipmentDestinationConfirmation` está preparada; no se aplicó a una base externa. No se acreditan búsquedas Geoapify reales ni mapa real sin sus claves. El usuario confirmó que no dispone de servicios y autorizó preparar entorno local; esas dependencias se registran por separado.
