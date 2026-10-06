# Flujo de trabajo para implementar un caso de uso

Esta guía aplica a los CU del backend y complementa las decisiones de [arquitectura](arquitectura-backend.md). La entrega de análisis y diseño asigna cada CU obligatorio a un módulo y enumera dependencias entre módulos; el detalle operativo se concreta al implementar cada CU.

## Antes de codificar

1. Identificar el CU, su módulo principal, actores, requisito de origen y criterio demostrable del hito. Las capacidades sin CU numerado también se especifican cuando se implementan.
2. Crear **un archivo Markdown por CU implementado** en `docs/cu/`, con nombre `CU-XX-nombre-breve.md`, a partir de la [plantilla](../cu/_plantilla.md). Completar sus reglas y decisiones reales; no dejar secciones con marcadores de posición en la PR.
3. Acordar las dependencias entre módulos y los contratos de entrada/salida. Registrar dudas que cambien una decisión transversal antes de codificarlas. No acceder directamente al repositorio privado de otro módulo para evitar definir contratos implícitos.
4. Precisar tenant, comercio y perfil autorizados; invariantes de dominio; estados afectados; persistencia y concurrencia; idempotencia, eventos o caché si aplican.

## Durante la implementación

- Ubicar reglas permanentes en el dominio y coordinación de la operación en aplicación. Mantener API y Worker como adaptadores; configurar EF Core y servicios externos en infraestructura.
- Validar entrada en el borde y reglas de negocio dentro del CU o dominio. Separar DTO HTTP de entidades. Propagar cancelación a operaciones de E/S y documentar los errores esperados del contrato.
- Cuando haya escritura y evento, diseñar la transacción y el outbox juntos. Cuando haya reintentos o llamadas repetidas, documentar cómo se evita duplicar efectos.
- Mantener el aislamiento en lectura y escritura y probarlo con contextos distintos. La existencia de una propiedad `OperadorId` no prueba aislamiento.
- Actualizar la especificación del CU si cambian reglas, contratos o decisiones durante la implementación.

## Antes de abrir una PR

La PR debe incluir el código, la especificación Markdown de cada CU implementado y la entrada correspondiente en la **única bitácora individual de cada integrante que trabajó en la tarea**. La bitácora se añade o actualiza antes de crear la PR, también si no se utilizó IA. La descripción de la PR enlaza las especificaciones y las bitácoras implicadas.

Verificar compilación y las pruebas pertinentes al cambio: invariantes del dominio, flujo de integración, aislamiento o arquitectura según el riesgo. Dejar en la especificación las pruebas ejecutadas y las limitaciones reales. Un CU se considera listo para revisión cuando otra persona puede entender su comportamiento y comprobar sus criterios de aceptación desde esos archivos.
