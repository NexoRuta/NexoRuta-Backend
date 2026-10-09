# NexoRuta Backend

Backend de la plataforma multioperador de distribución de última milla del Taller .NET 2026. Este repositorio usa .NET 10 y está organizado como base de un **monolito modular**, con un Worker independiente. La entrega de análisis y diseño del 04/10/2026 y el enunciado del laboratorio fundamentan las decisiones resumidas aquí.

> **Estado del recorte funcional (08/10/2026):** están implementados los recortes del primer monitoreo: [CU-01 — datos iniciales y acceso directo](docs/cu/CU-01-comercios-usuarios.md), [CU-07 — alta individual de envíos](docs/cu/CU-07-alta-envios.md) y [CU-08 — bulto](docs/cu/CU-08-bultos.md), con EF Core/PostgreSQL. Autenticación, CRUD de administración, RLS y mensajería siguen pendientes.

## Estructura y estado verificado

```text
NexoRuta.sln
src/
  NexoRuta.Api/             # API ASP.NET Core y OpenAPI de desarrollo
  NexoRuta.Worker/          # BackgroundService de plantilla
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
# Antes de ejecutar la suite completa, configurar NEXORUTA_TEST_POSTGRES
# y verificar el aislamiento indicado en la sección siguiente.
dotnet test NexoRuta.sln --no-build --no-restore --nologo
```

Para ejecutar la API y el Worker localmente:

```bash
dotnet run --project src/NexoRuta.Api/NexoRuta.Api.csproj --launch-profile http
dotnet run --project src/NexoRuta.Worker/NexoRuta.Worker.csproj
```

El perfil `http` de la API usa `http://localhost:5041`; OpenAPI de desarrollo está en `http://localhost:5041/openapi/v1.json`. La API necesita PostgreSQL mediante `ConnectionStrings:Postgres`; al arrancar aplica migraciones e inicializa los datos configurados en `AccesoInicial`. `GET /api/accesos?tipo=Comercio|Operador` alimenta los selectores; `GET /api/usuarios/actual` y `GET/POST /api/envios` requieren una selección válida mediante `X-NexoRuta-Acceso`. Se separan ámbitos de operador/comercio, sin perfiles laborales ni autenticación por credenciales en este recorte. Liveness/readiness están en `/health/live` y `/health/ready`. El Worker no abre un puerto HTTP.

El arranque tiene un único bootstrap, sin modo Demo. Las cuatro propiedades de AccesoInicial son obligatorias y no pueden estar vacías: UsuarioEmail, OperadorUsuarioEmail, OperadorNombre y ComercioNombre; los valores se recortan y los dos emails deben identificar cuentas distintas. Para sobrescribirlas por entorno, usá AccesoInicial__UsuarioEmail, AccesoInicial__OperadorUsuarioEmail, AccesoInicial__OperadorNombre y AccesoInicial__ComercioNombre. Demo:Enabled / Demo__Enabled ya no se interpretan: retiralas de configuraciones externas. El seeder no crea envíos ficticios ni renombra datos históricos por sus nombres. Si cambiás las identidades configuradas, puede crear cuentas y organizaciones adicionales; no fusiona ni elimina las anteriores. El nombre histórico nexoruta-demo del despliegue no selecciona un modo de ejecución.

El Compose local vive en el repositorio principal y publica la API en `http://localhost:5000` (puerto 8080 del contenedor). La inicialización puede repetirse sin duplicar los registros iniciales. Redis y RabbitMQ aún no participan del flujo de envíos.

## Pruebas PostgreSQL: destino aislado obligatorio

> **Advertencia:** no ejecutar la suite PostgreSQL sin configurar y comprobar
> `NEXORUTA_TEST_POSTGRES`. Si falta o está vacía, el código actual usa un fallback
> hacia `127.0.0.1:5432` con credenciales locales de desarrollo. No detecta si ese
> servidor es exclusivo de tests. En Windows ese endpoint puede corresponder a
> otro PostgreSQL; esta confusión causó el error de autenticación `28P01`.

1. Usar PostgreSQL 18 en un contenedor temporal **exclusivo**, independiente de
   Windows y de Compose compartido; publicar solo en loopback y un puerto libre
   (en las validaciones se utilizó `127.0.0.1:55432`).
2. Generar una credencial exclusiva, no versionarla ni imprimir la connection
   string. El rol requiere permiso para crear y eliminar las bases de pruebas.
   No reutilizar usuarios o bases de producción.
3. Definir `NEXORUTA_TEST_POSTGRES` con ese destino únicamente en el proceso de
   pruebas. Antes de ejecutar, comprobar autenticación, versión 18 e identidad
   de la instancia; no basta con que el puerto responda.
4. Ejecutar los comandos anteriores. `EnvioPostgresRoundTripTests` reemplaza el
   nombre de base por `nexoruta_test_<UUIDv7>`, aplica migraciones y ejecuta
   `EnsureDeletedAsync()` al terminar cada caso. Autorizar esa eliminación solo
   para las bases temporales creadas dentro de la instancia aislada.
5. Verificar que no quedaron bases temporales, restaurar la variable del proceso
   y retirar solo el contenedor creado para esta ejecución. No eliminar volúmenes
   ni modificar PostgreSQL de Windows o Compose compartido.

### Baseline conservado de EST-04

| Comprobación | Resultado local |
| --- | --- |
| Restore | Correcto |
| Build | 0 errores, 0 advertencias |
| Unitarias | 19/19 |
| Arquitectura | 3/3 |
| Integración | 42/42 |
| Configuración y DI | 17/17, incluidas en las 42 de integración; también ejecutadas por separado |

Son resultados de EST-04, no cantidades permanentes ni prueba de CI remota.
EST-05 solo actualiza documentación: no reejecutó las suites técnicas ni creó
otra instancia PostgreSQL. EST-02 a EST-04 validaron la integración con
PostgreSQL 18 temporal exclusivo; no se utilizaron bases persistentes compartidas.

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
se incorporan automáticamente al PR backend. No se implementaron autenticación
real ni RLS, ni se modificaron entidades Domain o migraciones en este acumulado.

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
