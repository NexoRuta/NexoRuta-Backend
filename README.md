# NexoRuta Backend

Platform organization administration: [authority, API and first-account procedure](docs/workflow/platform-administration.md). This is an additional backend slice, not completion of CU-01; Identity/OIDC and RLS remain pending; the separate Admin frontend is available in the frontend repository.

Backend de la plataforma multioperador de distribución de última milla del Taller .NET 2026. Este repositorio usa .NET 10 y está organizado como base de un **monolito modular**, con un Worker independiente. La entrega de análisis y diseño del 04/10/2026 y el enunciado del laboratorio fundamentan las decisiones resumidas aquí.

> **Estado actual:** están implementados los recortes del primer monitoreo: [CU-01 — autenticación y roles propios](docs/cu/CU-01-comercios-usuarios.md), [CU-07 — alta individual de envíos](docs/cu/CU-07-alta-envios.md) y [CU-08 — bulto](docs/cu/CU-08-bultos.md), con EF Core/PostgreSQL. M05 incorpora seguimiento público, recuperación de enlaces y Outbox transaccional con procesos separados para RabbitMQ y Kafka. CU-01 incorpora login JWT y administración de roles propios; RLS sigue pendiente.

## Estructura y estado verificado

```text
NexoRuta.sln
src/
  NexoRuta.Api/             # API ASP.NET Core y OpenAPI de desarrollo
  NexoRuta.Worker/          # Outbox RabbitMQ/Kafka y generación de seguimiento
  NexoRuta.Domain/          # Modelos iniciales de administración y envíos
  NexoRuta.Application/     # Casos de uso de envíos y contrato de usuario actual
  NexoRuta.Infrastructure/  # EF Core, migraciones, inicialización y acceso directo
tests/
  NexoRuta.UnitTests/
  NexoRuta.IntegrationTests/
  NexoRuta.ArchitectureTests/
Dockerfile
```

La solución compila con .NET 10 e incluye pruebas unitarias, integración PostgreSQL y pruebas de arquitectura. ArchitectureTests comprueba la independencia de las capas internas respecto de HTTP y persistencia y que las respuestas de envíos pertenezcan a la API. El workflow de backend compila y ejecuta las pruebas en PRs de `develop` hacia `main`; su presencia no acredita una ejecución remota. Worker registra actividad periódica y todavía no procesa envíos.

## Requisitos y comandos locales

- .NET SDK 10. En el checkout combinado, `global.json` de la raíz selecciona SDK `10.0.401`; un clon aislado de este submódulo no incluye ese selector.
- Docker Desktop/Engine para imágenes, el entorno Compose del repositorio principal o PostgreSQL temporal exclusivo de pruebas. Este repositorio no contiene un `compose.yaml`.

Desde la raíz de este repositorio:

```bash
dotnet restore NexoRuta.sln --nologo
dotnet build NexoRuta.sln --no-restore --nologo
# La integración utiliza contenedores temporales propios; requiere Docker.
# y verificar el aislamiento indicado en la sección siguiente.
dotnet test NexoRuta.sln --no-build --no-restore --nologo
```

Para ejecutar la API y el Worker localmente:

```bash
dotnet run --project src/NexoRuta.Api/NexoRuta.Api.csproj --launch-profile http
dotnet run --project src/NexoRuta.Worker/NexoRuta.Worker.csproj
```

El perfil `http` de la API usa `http://localhost:5041`; OpenAPI está en `/openapi/v1.json` solo en Development. Login: `POST /api/auth/login` con JWT Bearer, hashing Identity y permisos efectivos consultados en PostgreSQL en cada petición. `X-NexoRuta-Acceso` es compatibilidad exclusiva de Development, nunca un mecanismo de producción.

El esquema (`DatabaseInitialization:ApplyMigrations`) y el bootstrap (`DatabaseInitialization:SeedData`) son operaciones independientes, desactivadas por defecto. Habilitarlas es una operación administrativa explícita sobre el destino confirmado. No ejecutar migraciones ni reinicializaciones de la base de desarrollo como parte de los tests.

Cuando se habilita el bootstrap, configurar `AccesoInicial:OperadorId` y `ComercioId` con UUID persistentes, sus nombres y emails, contraseñas por secretos de entorno y, opcionalmente, `OperadorRolCodigo` / `ComercioRolCodigo`. `Bootstrap:Roles` admite una lista arbitraria con `Codigo`, `Nombre`, **uno** de `OperadorId` / `ComercioId` y `Permisos` (códigos globales). No hay cuatro roles obligatorios ni UUID/contraseñas predeterminados. El bootstrap identifica organizaciones por UUID, crea roles/grants solo durante la primera inicialización por organización y conserva nombres editados, revocaciones, roles eliminados, asignaciones y hashes existentes. Reparar el acceso de un usuario existente no le asigna privilegios automáticamente.

La gestión posterior usa `/api/roles`, `/api/roles/permisos`, `/api/roles/{id}/permisos` y `/api/roles/accesos/{accesoId}`. Las políticas de configuración/usuarios y la pertenencia de la cuenta delimitan cada operación. Los roles de comercio no dependen de ningún operador; el tenant operativo de envíos sigue siendo el operador.

## Pruebas PostgreSQL: destino aislado obligatorio

Las fixtures crean contenedores **propios de PostgreSQL 18**, verifican identidad del contenedor, base, usuario y versión antes de migrar y crean bases por escenario. No existe fallback a desarrollo ni se interpreta `NEXORUTA_TEST_POSTGRES`. El borrado de contenedores temporales elimina sus bases; no se ejecuta SQL de borrado contra una base externa. Docker debe estar disponible. La suite completa también usa servicios temporales de mensajería.

```powershell
dotnet restore NexoRuta.sln
dotnet build NexoRuta.sln --no-restore
dotnet test NexoRuta.sln --no-build --no-restore
```

CU-01 conserva pruebas de migración histórica/rollback, inicialización vacía, repetición del bootstrap, aislamiento de roles, revocación JWT y restricciones de pertenencia con SQL directo. Un rollback con códigos de rol repetidos entre organizaciones se rechaza transaccionalmente: requiere consolidación administrativa explícita, nunca fusiona permisos automáticamente.

## Alcance de la rama backend

La rama `feature/refactor-envios`, basada en `develop`, no es exclusivamente
estructural. Además de separar tipos y alinear namespaces, agrupa resultados de
Application, introduce contratos HTTP propios y mapeo explícito, refuerza
validaciones, elimina Demo y explicita EF Core Relational 10.0.12 en Infrastructure.
Cambiaron contratos internos de Application; no se afirma compatibilidad binaria.
Las propiedades JSON de los endpoints existentes se mantienen mediante el mapeo;
la corrección cultural de límites decimales no implica OpenAPI idéntico.

El [alcance del refactor](docs/workflow/refactor-envios.md) distingue estas unidades
y sus límites. Los cambios de frontend se revisan en su propio repositorio y no
se incorporan automáticamente al PR backend. Ese refactor histórico no incorporaba autenticación real ni RLS; CU-01 ahora agrega JWT, roles propios y sus migraciones. RLS sigue pendiente.

## Decisiones de arquitectura

| Tema | Decisión o requisito vigente |
| --- | --- |
| Organización | Monolito modular .NET 10 por capacidad de negocio; Worker separado para procesamiento asíncrono. |
| Dependencias | `Domain` no depende de frameworks, datos ni presentación; `Application` depende de `Domain`; `Infrastructure` implementa contratos de aplicación; API y Worker son adaptadores. Una prueba de arquitectura debe proteger esta regla en CI. |
| Persistencia | PostgreSQL y EF Core con migraciones. El operador es el tenant; una base y tablas compartidas con RLS como segunda barrera. Cada envío guarda el operador elegido y el comercio de origen, sin vínculo previo entre ambos; las cuentas de comercio consultan sus propios envíos. |
| Modelo inicial | IDs `Guid`/UUIDv7; `Destinatario` y `Direccion` separados; peso del `Bulto` en gramos y dimensiones en centímetros. Estas decisiones fueron acordadas para el primer corte. |
| Reglas centrales | Estados de envío explícitos, eventos inmutables, configuración tarifaria y operativa versionada, concurrencia optimista e idempotencia donde correspondan. |
| Integraciones | Cola de trabajo, patrón outbox y Worker idempotente son obligatorios. RabbitMQ es la propuesta; Redis, SignalR, Serilog y OpenTelemetry figuran en el diseño para caché, tiempo real y observabilidad. |
| Calidad | Pruebas unitarias de dominio, integración de un flujo crítico, arquitectura y aislamiento entre operadores en el pipeline. |

La [guía de arquitectura del backend](docs/workflow/arquitectura-backend.md) detalla límites, seguridad multioperador, contratos entre módulos, decisiones adoptadas, propuestas y decisiones todavía pendientes. Léela antes de implementar un módulo o CU; evita tomar una elección pendiente como si ya hubiera sido acordada.

El [catálogo funcional y de monitoreos](docs/workflow/alcance-funcional.md) resume los 30 CU obligatorios, las capacidades adicionales de las aplicaciones y los hitos. Está incluido para quienes no tengan la documentación original; cada CU necesita su propia especificación al implementarse.

## Módulos de negocio

| Módulo | Responsabilidad principal |
| --- | --- |
| M01 Administración y configuración | Operadores, comercios, usuarios, zonas, franjas, tarifas, reglas y marca. |
| M02 Envíos y depósito | Alta, bultos, recepción, estado e historial del envío. |
| M03 Planificación, flota y despacho | Repartidores, vehículos, rutas y restricciones de asignación. |
| M04 Ejecución y sincronización móvil | Jornada del repartidor, evidencias, devoluciones y trabajo sin conexión. |
| M05 Seguimiento y comunicaciones | Consulta pública, reprogramación, notificaciones y avisos a comercios. |
| M06 Monitoreo y liquidaciones | Tablero en vivo, reportes e información de liquidación. |

Cada CU tiene un módulo principal. Las dependencias entre módulos se resuelven mediante **contratos explícitos**, sin acceder directamente a los detalles de persistencia de otro módulo. Los controladores HTTP deben permanecer delgados; las reglas del negocio pertenecen al dominio o a la aplicación. El DBML entregado es preliminar: no se crean clases vacías para todas sus tablas ni se toma como esquema físico definitivo.

## Implementar un CU y abrir una PR

1. Seguir la [guía de implementación de CU](docs/workflow/implementar-cu.md) y crear **un Markdown de especificación por cada CU que se implemente** en `docs/cu/CU-XX-nombre-breve.md`, usando la [plantilla](docs/cu/_plantilla.md). No se crean archivos de CU que todavía no se hayan trabajado.
2. Documentar objetivo, actores, entradas/salidas, flujo principal y alternativas, reglas, seguridad por operador/comercio, dependencias, criterios de aceptación, pruebas y decisiones pendientes. Actualizar ese archivo si el CU cambia durante el desarrollo.
3. **Antes de crear la PR, cada integrante que participó agrega una entrada fechada de esa tarea a su única bitácora individual** en `docs/bitacora-ia/<integrante>.md`. No se crea una bitácora nueva por tarea. Incluso sin uso de IA se registra la tarea y se indica expresamente «No se usó IA».
4. Cuando se usó IA, la entrada distingue finalidad y herramienta, decisiones del integrante y del asistente, qué se aceptó sin modificar, qué se cambió y por qué, qué se descartó y por qué, implementación, verificaciones y límites. La descripción de la PR enlaza tanto la especificación de cada CU como las bitácoras individuales de quienes participaron.

La [bitácora individual de Zarsu - Franco](docs/bitacora-ia/zarsu-franco.md) muestra el formato acumulativo. El registro de uso de IA por componente responde al §9.5 de `Laboratorio__NET_2026.pdf`.

Las organizaciones ya existentes al aplicar la normalización se marcan como inicializadas: su configuración posterior debe administrarse explícitamente, sin volver a ejecutar plantillas iniciales sobre decisiones históricas.
