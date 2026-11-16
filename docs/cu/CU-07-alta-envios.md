# CU-07 — Alta de envíos

- **Estado**. Implementados el alta individual por API y la importación CSV idempotente.
- **Módulo**. M02 — Envíos y depósito.
- **Fuente**. Laboratorio .NET 2026, §5.2, CU-07.

## Actor y flujo

El usuario autenticado del comercio elige un operador disponible para cada envío. La API obtiene el comercio y el usuario del acceso autenticado. La selección del operador se guarda en el envío y puede variar en el siguiente. Las escrituras requieren una cuenta de comercio con el permiso `envios.crear`. El listado usa `envios.consultar` y el contexto del comercio o del operador autenticado.

Al admitir un envío nuevo, el Backend verifica el servicio y las reglas operativas publicadas, fija la tarifa aplicada y confirma el destino inicial. El envío, sus bultos y los registros de Outbox se guardan juntos. El procesamiento posterior de seguimiento y eventos se describe en el [ADR de consistencia y Outbox](../adr/consistencia-y-outbox.md).

## Contrato HTTP

| Operación | Entrada y resultado |
| --- | --- |
| `GET /api/operadores` | Devuelve los operadores disponibles para el comercio. |
| `POST /api/envios` | Recibe JSON con `operadorId`, nombre y correo del destinatario, dirección, servicio seleccionado y `bultos`. Devuelve `201 Created` con el ID del envío, sus bultos y la tarifa aplicada. |
| `GET /api/operadores/importacion-envios` | Devuelve el catálogo de operadores para el selector de importación. |
| `POST /api/envios/importaciones` | Recibe `multipart/form-data` con `versionFormato=2`, `operadorId` y `archivo`. Devuelve un resumen y el resultado por fila. |
| `GET /api/envios` | Lista los envíos visibles para el comercio o el operador autenticado. Incluye los bultos de cada envío. |

[Controlador de envíos](../../src/NexoRuta.Api/Features/Envios/Controllers/EnviosController.cs) · [Alta JSON](../../src/NexoRuta.Api/Features/Envios/Contracts/Requests/CrearEnvioRequest.cs) · [Respuesta de alta](../../src/NexoRuta.Api/Features/Envios/Contracts/Responses/EnvioCreado.cs) · [Formulario de importación](../../src/NexoRuta.Api/Features/Envios/Contracts/Requests/ImportarEnviosRequest.cs) · [Respuesta de importación](../../src/NexoRuta.Api/Features/Envios/Contracts/Responses/ImportacionEnviosResponse.cs) · [Operadores disponibles](../../src/NexoRuta.Api/Features/Comercios/Controllers/OperadoresDisponiblesController.cs)

## Importación CSV e idempotencia

El CSV usa UTF-8, separador coma, un máximo de 5 MiB y hasta 500 registros de datos. Cada fila declara un bulto. Las filas con igual referencia y datos de destinatario, dirección y servicio compatibles se agrupan en un envío. Un archivo declara un único operador. El encabezado de la versión 2 es el siguiente. [Parser CSV](../../src/NexoRuta.Application/Envios/Importacion/CsvEnviosParser.cs)

```csv
referenciaExterna,nombreDestinatario,emailDestinatario,direccion,codigoBulto,pesoGramos,largoCentimetros,anchoCentimetros,altoCentimetros,zonaId,latitud,longitud,modalidad
```

- **Misma declaración**. La identidad se busca por comercio y referencia externa normalizada. La equivalencia se comprueba con el operador y la huella versionada de la declaración original. Reenviar el archivo completo o un subconjunto de envíos con la misma declaración devuelve los IDs existentes. Para un envío con varios bultos, la declaración equivalente incluye el conjunto completo de bultos, en cualquier orden.
- **Referencias nuevas**. Un lote válido puede mezclar envíos ya existentes con otros nuevos. Se escriben únicamente los nuevos. Los bloqueos transaccionales serializan importaciones con referencias coincidentes.
- **Conflicto o error**. Una referencia existente con otro operador o declaración, o filas incompatibles dentro del mismo archivo, produce `409`. Un rechazo por validación de filas o admisión devuelve `422`. En ambos casos se rechaza el lote de forma atómica. Un archivo o formulario inválido puede devolver `400`, y superar los límites de tamaño o registros devuelve `413`.
- **Respuesta exitosa**. `200 OK` con estado `Completada`. La fila canónica de cada envío indica `Creado` o `YaExistente` y su `envioId`. Las filas restantes del mismo grupo indican `DuplicadoDe` y apuntan al registro canónico con `mismoQueRegistro`.

[Caso de uso de importación](../../src/NexoRuta.Application/Envios/Importacion/ImportarEnviosUseCase.cs) · [Cálculo de la huella](../../src/NexoRuta.Application/Envios/Importacion/HuellaSolicitudEnvio.cs)

## Pruebas relacionadas

- [CrearEnvioUseCaseTests](../../tests/NexoRuta.UnitTests/Envios/CrearEnvioUseCaseTests.cs) y [EnvioPostgresRoundTripTests](../../tests/NexoRuta.IntegrationTests/Envios/EnvioPostgresRoundTripTests.cs) cubren la admisión individual, la elección de operador y la persistencia.
- [ShipmentCsvImportHttpTests](../../tests/NexoRuta.IntegrationTests/Envios/ShipmentCsvImportHttpTests.cs) cubre OpenAPI multipart, reimportación completa y parcial, respuesta perdida después del commit, conflictos, concurrencia y rollback de envíos, identidades y Outbox.
- [RlsRestrictedHttpFlowTests](../../tests/NexoRuta.IntegrationTests/Seguridad/RlsRestrictedHttpFlowTests.cs) cubre la visibilidad entre organizaciones con un rol PostgreSQL restringido.

Ver también [CU-08 — Bultos](CU-08-bultos.md), [destinos y geocodificación](destinos-envios.md) y [cancelación de envíos](cancelacion-envios.md).

Las medidas declaradas en alta, retiro, cotización y CSV usan la
[regla compartida de bultos](CU-08-bultos.md#regla-compartida-de-medidas).
La validación JSON conserva 400 por campo; CSV conserva 422 por fila con sus
códigos y mensajes de sintaxis, rango o precisión, antes de la admisión atómica.
