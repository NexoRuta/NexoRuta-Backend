# Destino confirmado para la hoja móvil

M02 conserva la dirección y su confirmación. M03 planifica con destinos confirmados. La búsqueda de destinos usa Mapbox temporal y la selección se confirma explícitamente al admitir el envío o al actualizar su dirección. [CU-07 — Alta de envíos](CU-07-alta-envios.md)

## Flujo

Antes del alta, el comercio puede consultar sugerencias sin un ID de envío. El alta actual registra la dirección y confirma las coordenadas del servicio en la transacción de admisión. Para un envío existente, la consulta de sugerencias devuelve opciones sin escribir. La confirmación usa un `PUT` separado y verifica la versión de la dirección. Si el proveedor está indisponible, la API permite presentar coordenadas ingresadas manualmente mediante el contrato de confirmación.

## API vigente

Todas las operaciones requieren Bearer de comercio y permiso vigente `envios.crear`. El servidor deriva comercio y usuario de la identidad; otro comercio recibe 404 y un operador, 403.

- `GET /api/envios/{id}/destino`: dirección, versión **de la dirección**, estado, coordenadas y fecha de confirmación. Coordenadas/fecha nulas significan sin confirmar.
- `POST /api/envios/destino/busqueda` admite una consulta antes del alta. `POST /api/envios/{id}/destino/busqueda` busca para un envío existente, verificando pertenencia y estado. Ambos reciben JSON con `consulta` de 3–240 caracteres, con un mínimo de tres tras recortar espacios.
- `POST /api/envios/{id}/destino/propuestas`: sin dirección elegida por el visitante; busca la dirección almacenada del envío propio. Devuelve dirección/versión consultadas, atribución y propuestas. 200 con lista vacía significa sin coincidencias; 503 significa proveedor indisponible; 429 limita consultas. No escribe ubicaciones.
- `PUT /api/envios/{id}/destino`: `{ "version": 0, "latitud": -34.9, "longitud": -56.2, "direccion": "Dirección seleccionada" }`. Versión y coordenadas son obligatorias; `direccion` opcional conserva el texto actual cuando es null (selección manual). Si se envía, debe tener 1–240 caracteres no vacíos. No acepta campos extra. Texto y coordenadas se guardan atómicamente con un único incremento de versión; no se almacena la respuesta completa del proveedor. 204 confirma, 400 entrada inválida, 404 fuera de cuenta y 409 versión/estado cambiados.

La confirmación inicial se admite mientras el envío está `Admitido` o, si ya fue recibido, mientras sigue `EnDeposito` y el destino aún está pendiente. La recepción no lo confirma automáticamente; la planificación sigue exigiendo coordenadas y confirmación expresa. Si el destino ya estaba confirmado, todavía puede corregirse mientras el envío permanezca `Admitido`, como antes; después de la recepción no se puede cambiar. Los estados posteriores —incluido `AsignadoRuta`— tampoco admiten cambios. El PDF original (§5.2 req. 10 y §5.3 reqs. 13–17) y la planilla (`Dependencias!G7:H8`) no exigen confirmar antes de recibir; el requisito operativo conservado es confirmar antes de planificar.

El dominio verifica coordenadas finitas y rangos. La función SQL `confirm_shipment_destination` revalida acceso/permiso, pertenencia, versión y estado bajo el mismo bloqueo del operador que recepción/planificación; tras recibir, además exige que el destino siga pendiente al tomar el bloqueo. Su propietario es NOLOGIN/NOBYPASSRLS y dispone únicamente de las columnas necesarias. La API no obtiene acceso general a rutas u operadores para confirmar direcciones. Dos confirmaciones de una misma versión no se pisan: una confirma y la otra recibe 409.

La responsabilidad se retiró de `GestionarRutasUseCase`/`IRutasRepository` y quedó en `GestionarDestinoEnvioUseCase`/`IDestinosEnvioRepository`, sin cambiar el contrato HTTP de confirmación ni introducir alias.

[Controlador de destinos](../../src/NexoRuta.Api/Features/Envios/Controllers/DestinosEnvioController.cs) · [Contrato de búsqueda](../../src/NexoRuta.Api/Features/Envios/Contracts/Requests/BuscarDestinoRequest.cs) · [Contrato de confirmación](../../src/NexoRuta.Api/Features/Envios/Contracts/Requests/ConfirmarDestinoRequest.cs)

## Integraciones y configuración vigentes

Las búsquedas de alta y de envíos existentes usan el adaptador de **Mapbox temporal** del Backend. Consulta el endpoint forward v6 con `permanent=false`, hasta cinco resultados y un máximo de 64 KiB de respuesta. Las sugerencias se devuelven para la selección interactiva. El Backend guarda la dirección y las coordenadas declaradas en la admisión o confirmación, sin guardar la respuesta del proveedor ni cachear sus sugerencias. [Adaptador Mapbox](../../src/NexoRuta.Infrastructure/Envios/Geocodificacion/GeocodificadorMapboxTemporal.cs)

El adaptador requiere `Geocoding:MapboxTemporal:Habilitado=true` y un `Mapbox:PublicAccessToken` que empiece con `pk.`. La configuración versionada trae el flag deshabilitado. Las propuestas de la base operativa usan el adaptador Geoapify, habilitado por separado con `Geocoding:Geoapify:PermitirLlamadasReales` y `Geocoding:Geoapify:ApiKey`. Las estimaciones viales tienen otra configuración. [Registro de ambos adaptadores](../../src/NexoRuta.Infrastructure/Persistence/DependencyInjection.cs) · [Configuración de la API](../../src/NexoRuta.Api/appsettings.json)

| Configuración compartida bajo `Geocoding:Consumo` | Predeterminado | Restricción |
| --- | ---: | --- |
| `MaxSolicitudes24Horas` | 500 | 1–2000, ventana móvil global de 24 horas |
| `SolicitudesPorSegundo` | 3 | 1–3, ventana móvil global de un segundo |
| `SolicitudesPorMinutoUsuario` | 10 | 1–60, cuota HTTP por usuario de la API |
| `TimeoutSegundos` | 8 | 1–30, incluye reserva y llamada al proveedor |

El contador de consumo usa PostgreSQL y se comparte entre procesos. La reserva se confirma antes de llamar al proveedor y se conserva si se cancela o falla la solicitud. La API traduce el agotamiento de cuota y el límite de frecuencia a `429` con `Retry-After`; los fallos de disponibilidad devuelven `503`. Las variables de entorno usan `__` para separar secciones. Los límites internos controlan el consumo de este proyecto. Las condiciones de la cuenta del proveedor y del despliegue requieren verificación operativa. [Contador compartido](../../src/NexoRuta.Infrastructure/Envios/Geocodificacion/ConsumoGeocodificacion.cs)

## Evidencia y evolución

- [DestinosEnvioHttpTests](../../tests/NexoRuta.IntegrationTests/Envios/DestinosEnvioHttpTests.cs) cubre acceso con rol restringido, cuentas ajenas, confirmación concurrente, recepción y validación del contrato.
- [GeocodificadorMapboxTemporalTests](../../tests/NexoRuta.UnitTests/Envios/GeocodificadorMapboxTemporalTests.cs) cubre el modo temporal, los requisitos de configuración, la reserva de consumo y la respuesta ante datos inválidos con un proveedor simulado.
- [ConsumoGeocodificacionPostgresTests](../../tests/NexoRuta.IntegrationTests/Envios/ConsumoGeocodificacionPostgresTests.cs) cubre el presupuesto en PostgreSQL aislado.
- El corte del 01/11/2026 introdujo búsqueda Geoapify para destinos y el contador persistente. El selector vigente pasó a Mapbox temporal para destinos y reutiliza ese contador. [Evidencia histórica del contador](../../../docs/auditorias/geocodificacion/resultado.md) · [Evolución del selector](../../../docs/auditorias/geocodificacion/03-selector-guardar-reabrir.md)

### Traducción HTTP de errores de búsqueda

`DestinosEnvioController.EjecutarGeocodificacionAsync` concentra únicamente
`GeocodificacionNoDisponibleException` (503 con su detalle) y
`GeocodificacionLimiteException` (429). Lo usan `Proponer`, `Buscar` y
`BuscarParaAlta`; cada acción conserva su validación, 404/409 y respuesta exitosa.
Las cancelaciones y las demás excepciones se propagan. Las respuestas de estas
acciones conservan `Cache-Control: no-store` y los cuerpos `ProblemDetails`.

- Límite temporal del consumo: `Retry-After: 1`, `code: geocoding_rate_limited`.
- Cuota agotada: `Retry-After: 86400`, `code: geocoding_quota_exhausted`.
  El adaptador vigente clasifica también los HTTP 402/429 externos como cuota agotada.
- Indisponibilidad: 503, sin `code` ni `Retry-After` del proveedor.

El middleware de la política `geocodificacion` puede rechazar antes de ejecutar
la acción: su 429 tiene cuerpo vacío y el `Retry-After` de su ventana HTTP.
No es el `ProblemDetails` con `code` del proveedor/consumo compartido.
`BusquedaDestinoSimuladaHttpTests` verifica estados, cuerpos y encabezados en las
tres acciones sin consumo externo; `DestinosEnvioHttpTests` verifica además el
rechazo real del middleware y los flujos con PostgreSQL restringido.
