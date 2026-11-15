# CU-08 — Bultos

- **Estado**. Implementado el registro de uno o más bultos por envío en el alta individual y en la importación CSV.
- **Módulo**. M02 — Envíos y depósito.
- **Fuente**. Laboratorio .NET 2026, §5.2, CU-08.

## Funcionamiento y reglas

En el alta individual, `POST /api/envios` recibe la colección `bultos` con al menos un elemento. En la importación CSV de [CU-07](CU-07-alta-envios.md), cada fila declara un bulto. Varias filas compatibles con la misma referencia externa pueden aportar bultos distintos a un solo envío. Una fila equivalente repetida aporta el mismo bulto una sola vez.

Cada bulto tiene un código obligatorio de hasta 80 caracteres, peso en gramos y largo, ancho y alto en centímetros. Los códigos deben ser distintos dentro de un envío. Cada medida está entre `0.01` y `999999999`, con hasta dos decimales efectivos. Los ceros finales son válidos cuando el valor cabe en esa precisión, como `1.2300`. La API valida la entrada y el dominio protege las medidas para los distintos caminos de creación. [Contrato de entrada](../../src/NexoRuta.Api/Features/Envios/Contracts/Requests/CrearEnvioRequest.cs) · [Entidad Bulto](../../src/NexoRuta.Domain/Envios/Bulto.cs)

El Backend asigna un UUIDv7 a cada bulto. PostgreSQL vincula cada bulto con su envío y operador mediante una clave foránea compuesta. El envío y todos sus bultos se persisten en la misma transacción. Si falla uno de los bultos durante el alta, la solicitud se rechaza sin dejar un envío parcial. [Persistencia de envíos](../../src/NexoRuta.Infrastructure/Envios/Repositories/EfEnviosRepository.cs) · [Migración de admisión de múltiples bultos](../../src/NexoRuta.Infrastructure/Persistence/Migrations/20261102020000_AdmitMultipleParcels.cs)

## Contratos y pruebas

- El alta individual y el listado devuelven `bultos` como colección. Cada elemento incluye ID, código y medidas. El CSV devuelve resultados por fila y permite consultar el envío admitido en el listado. [Respuesta de alta](../../src/NexoRuta.Api/Features/Envios/Contracts/Responses/EnvioCreado.cs) · [Respuesta del listado](../../src/NexoRuta.Api/Features/Envios/Contracts/Responses/EnvioDetalle.cs)
- `GET /api/envios/{id}/etiquetas` devuelve un PDF con una etiqueta por bulto persistido para los actores autorizados. [Controlador de envíos](../../src/NexoRuta.Api/Features/Envios/Controllers/EnviosController.cs) · [EtiquetasEnvioHttpTests](../../tests/NexoRuta.IntegrationTests/Envios/EtiquetasEnvioHttpTests.cs)
- [ShipmentCsvImportHttpTests](../../tests/NexoRuta.IntegrationTests/Envios/ShipmentCsvImportHttpTests.cs) verifica la persistencia de varios bultos por JSON y CSV, el reenvío con filas reordenadas y el rechazo atómico cuando el segundo bulto del alta individual es inválido.
- [PrecisionCargaTests](../../tests/NexoRuta.UnitTests/Envios/PrecisionCargaTests.cs), [CrearEnvioRequestTests](../../tests/NexoRuta.IntegrationTests/Envios/CrearEnvioRequestTests.cs) y [CapacidadVigenteHttpTests](../../tests/NexoRuta.IntegrationTests/Reparto/CapacidadVigenteHttpTests.cs) cubren precisión efectiva, límites del contrato HTTP y esquema OpenAPI.
