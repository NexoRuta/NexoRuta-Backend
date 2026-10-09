# Arquitectura y decisiones vigentes del backend

Este documento resume las decisiones de la entrega de análisis y diseño del 04/10/2026 y las convenciones acordadas para este repositorio. **Una decisión de diseño no significa que ya esté implementada.** Ante una diferencia con un requisito de la letra, prevalece `Laboratorio__NET_2026.pdf`; las dudas de implementación se resuelven con el equipo y se registran en la especificación del CU o en un ADR.

Fuentes de referencia: `Laboratorio__NET_2026.pdf` (§§ 3–6 y 8), `Entrega_Analisis_Diseno_2026-10-04.pdf` (pp. 8–14, ADR-001 y ADR-002), `Casos de Uso - Responsables.xlsm` (hojas `Módulos`, `Dependencias` y `No funcionales`) y `modelo_dominio_preliminar.dbml`. Estos archivos fueron usados para el análisis; este resumen queda en el repositorio para que el equipo pueda trabajar sin depender de su ubicación local.

## Estado del repositorio y dirección de dependencias

La solución apunta a **.NET 10** y contiene `NexoRuta.Domain`, `NexoRuta.Application`, `NexoRuta.Infrastructure`, `NexoRuta.Api`, `NexoRuta.Worker` y proyectos de pruebas unitarias, de integración y de arquitectura. La API implementa los recortes CU-01, CU-07 y CU-08 con persistencia y acceso por organización; el Worker sigue siendo una plantilla de actividad periódica. La existencia de un proyecto o paquete no implica que las capacidades posteriores estén terminadas.

| Proyecto | Responsabilidad y dependencias permitidas |
| --- | --- |
| `Domain` | Entidades, valores e invariantes de negocio. No depende de otros proyectos, EF Core, PostgreSQL ni ASP.NET. |
| `Application` | Casos de uso, coordinación y contratos que necesita la aplicación. Depende de `Domain`. |
| `Infrastructure` | Persistencia e integraciones que implementan contratos de aplicación. Depende de `Application` y `Domain`. |
| `Api` | Entrada HTTP, autenticación/autorización y composición de dependencias. Depende de `Application` e `Infrastructure`. |
| `Worker` | Proceso independiente para trabajo asíncrono. Depende de `Application` e `Infrastructure`; su integración con el resto se diseña mediante la cola. |

ADR-001 adopta un **monolito modular** para API y aplicaciones web, organizado por capacidad de negocio, con un Worker desplegado por separado. Dentro de cada módulo se distinguen dominio, casos de uso y adaptadores. Las dependencias de código apuntan hacia dominio y aplicación. La prueba automática de arquitectura debe fallar si el dominio comienza a depender de infraestructura o presentación.

## Módulos y propiedad funcional

| Módulo | Alcance principal | CU numerados |
| --- | --- | --- |
| M01 Administración y configuración | Operadores, comercios, usuarios, zonas, franjas, tarifas, reglas y marca. | CU-01 a CU-05 |
| M02 Envíos y depósito | Alta, bultos, precio aplicado, recepción, estados y trazabilidad. | CU-07 a CU-12 |
| M03 Planificación, flota y despacho | Repartidores, vehículos, rutas, restricciones y despacho. | CU-06 y CU-13 a CU-17 |
| M04 Ejecución y sincronización móvil | Carga, entrega, intentos fallidos, devoluciones, ubicación y operación sin conexión. | CU-18 a CU-24 |
| M05 Seguimiento y comunicaciones | Consulta pública, reprogramación y avisos a destinatarios y comercios. | CU-25 a CU-28 |
| M06 Monitoreo y liquidaciones | Tablero en vivo, indicadores, reportes y liquidación. | CU-29 y CU-30 |

Cada CU tiene **un módulo principal**, aunque pueda consumir contratos de otro. La matriz `Dependencias` de la planilla identifica contratos por acordar; no autoriza a un módulo a modificar directamente las tablas o entidades internas de otro. Documentar el proveedor, el contrato y la regla de consistencia antes de integrar un flujo transversal. Las capacidades de la sección 4 sin CU numerado, como etiquetas, incidencias o cuenta corriente, también forman parte del alcance y deben especificarse cuando se implementen.

## Reglas de implementación acordadas

- Organizar el código por módulo dentro de los proyectos existentes. Agregar un proyecto nuevo solo si existe una necesidad técnica o de aislamiento concreta.
- Mantener controladores y adaptadores HTTP limitados a entrada, autorización, invocación del CU y respuesta. Las reglas de negocio viven en dominio o aplicación, según su alcance.
- Agrupar las excepciones de aplicación de Administración en `Administracion/Excepciones`, con un archivo por tipo y el namespace correspondiente. La API conserva la traducción a estados HTTP.
- Definir entradas y salidas explícitas de cada CU. Los contratos HTTP no son entidades de dominio. No se ha decidido usar MediatR ni otro framework de mediación.
- Separar las consultas de pertenencia de usuarios (`IAccesosUsuarioRepository`) de las consultas de empresas (`IOperadoresRepository` e `IComerciosRepository`). Las proyecciones de contexto o detalle pueden incluir nombres de entidades relacionadas en una única consulta.
- Mantener la configuración de EF Core, migraciones, RLS y clientes externos en infraestructura. El dominio no lleva anotaciones de persistencia.
- Evitar clases vacías para todo el DBML: es un modelo conceptual preliminar. Implementar entidades y relaciones cuando un CU las necesite, conservando los invariantes ya conocidos.
- En el primer corte se acordó `Guid`/UUIDv7 generado al construir las entidades; `Destinatario` y `Direccion` son entidades separadas; `Bulto` expresa peso en gramos y dimensiones en centímetros. La conversión visual de unidades pertenece a la presentación. Registrar cualquier cambio transversal antes de alterar estos contratos.

## Multitenancy y seguridad de datos

ADR-002 adopta **PostgreSQL con base y tablas compartidas y Row-Level Security (RLS)**. El operador logístico es el tenant. Un comercio puede enviar con cualquier operador registrado, sin un vínculo comercial previo. Cada envío conserva `OperadorId` y `ComercioId`; las cuentas pertenecen a un operador o a un comercio y las lecturas se filtran por esa organización. `Usuario` y `Comercio` pueden ser identidades globales; los datos operativos deben conservar el contexto del operador y, cuando aplique, del comercio.

- Resolver el comercio desde la cuenta validada en el servidor y comprobar la existencia del operador elegido para cada envío. Un ID enviado por el cliente no concede acceso por sí solo.
- Comprobar pertenencia al mismo operador al relacionar envío, destinatario, dirección, bulto, ruta u otros datos. Proteger **lecturas y escrituras**; el filtrado de API/EF no sustituye RLS.
- El rol normal de la aplicación no debe ser dueño de tablas, superusuario ni tener `BYPASSRLS`. Usar credenciales separadas para migraciones. Establecer el contexto validado en la misma transacción; sin contexto, denegar acceso. Definir políticas `USING` y `WITH CHECK`, y `FORCE ROW LEVEL SECURITY` donde corresponda.
- Aplicar el mismo criterio al Worker y al seguimiento público, resolviendo el contexto desde mensajes confiables o enlaces opacos validados. No exponer IDs internos como mecanismo de acceso público.
- Probar con dos operadores y comercios con envíos: lectura, inserción, actualización, borrado, SQL directo, importaciones, Worker, acceso público y reutilización de conexiones. Ejecutar esas pruebas con el rol restringido real de la aplicación.

El mecanismo concreto de contexto RLS, sus migraciones y las pruebas todavía deben implementarse. No se debe dar por conseguido el aislamiento por tener `OperadorId` en una clase.

## Invariantes y requisitos que afectan a los CU

- Un envío tiene **uno o más bultos**. Cada bulto tiene identificación, peso y dimensiones. El alta masiva futura debe ser idempotente incluso ante reimportación parcial.
- El estado del envío sigue una máquina explícita: no se modifica arbitrariamente desde controladores, Worker o sincronización móvil. Cada transición genera un evento inmutable con fecha, origen, responsable y ubicación cuando corresponda. La propuesta incorpora estados de validación de prueba que aún requieren cerrar sus reglas.
- Tarifas y reglas operativas son configurables y versionadas. Un envío conserva la tarifa y el tratamiento aplicados al registrarse; una edición posterior no debe alterarlo retroactivamente.
- La asignación de un envío a rutas exige control de concurrencia para impedir doble asignación. EF Core y migraciones son obligatorios; se requiere demostrar concurrencia optimista sobre entidades susceptibles de edición simultánea.
- La sincronización sin conexión necesita política documentada de conflictos e idempotencia de eventos. No definirla implícitamente en un controlador.
- Publicar eventos de negocio mediante outbox; el Worker debe consumir de forma idempotente, con reintentos crecientes y cola de fallidos. La consistencia exacta y el contrato de mensajes aún requieren ADR y especificación por CU.

## Infraestructura y decisiones pendientes

| Tema | Estado de la decisión |
| --- | --- |
| PostgreSQL + EF Core/migraciones + RLS | PostgreSQL, EF Core y migraciones implementados para el primer monitoreo; RLS y su verificación con rol restringido siguen pendientes. |
| Cola de trabajo y Worker independiente | RabbitMQ y Worker están desplegados localmente; publicación, consumo y reintentos de mensajes siguen pendientes. |
| Caché distribuida | Obligatoria en dos perfiles de acceso, con invalidación y métricas; Redis está propuesto. |
| SignalR, Serilog y OpenTelemetry | La API exporta OpenTelemetry al Dashboard local; tiempo real, Serilog y correlación con mensajes siguen pendientes. |
| Kafka y NoSQL | Opcionales seleccionados en la entrega; no sustituyen la cola ni la base transaccional obligatorias. |
| Dos instancias API, Docker Compose, CI/CD, nube e IaC | Compose y workflows de CI/CD presentes; compilación y servicios locales verificados. Dos instancias, CI remota, despliegue e IaC no se verificaron en esta revisión. |
| Proveedor de identidad, modo de Blazor, conflicto offline, invalidación de caché, estrategia concreta de outbox, herramienta IaC y tablero técnico | Pendientes de decisión documentada antes de su implementación. |

Las pruebas unitarias de dominio, de integración de un flujo crítico, de arquitectura y de aislamiento deben ejecutarse en CI. El alcance y criterio verificable de cada CU se documentan siguiendo [Implementar un CU](implementar-cu.md) y su [plantilla](../cu/_plantilla.md).
