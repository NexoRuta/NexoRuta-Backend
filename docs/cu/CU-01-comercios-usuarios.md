# CU-01 — usuarios, roles propios y autorización

## Modelo vigente

Cada `Rol` pertenece exactamente a un operador o comercio **concreto** mediante UUID. Su código es único dentro de esa organización y tipo; no existe un catálogo obligatorio de roles. `Permiso` es un catálogo global de funcionalidades. `RolesPermisos` y las asignaciones de usuarios son decisiones administrativas persistentes.

Application verifica permisos del actor y delimita consultas/escrituras a su organización. PostgreSQL refuerza la pertenencia mediante CHECK, FK a organizaciones y FK compuesta de acceso/rol por ID, tipo de organización e identidad generada. Ni asignaciones por SQL directo ni UUID coincidentes de operador/comercio permiten cruzar ámbitos. El comercio conserva roles propios independientes de los operadores con los que trabaja; el operador sigue siendo el tenant operativo de envíos.

## Inicialización controlada

- Migración de esquema y bootstrap se habilitan por separado (`DatabaseInitialization:ApplyMigrations` / `SeedData`), por defecto `false`.
- `AccesoInicial` requiere IDs persistentes de organizaciones; los nombres son editables, no identidad.
- `Bootstrap:Roles` configura una lista arbitraria de códigos/nombres/propietario/permisos. Los roles y grants iniciales se crean una sola vez por organización, con un marcador persistido; eliminaciones y modificaciones posteriores no se restauran.
- Las contraseñas de usuarios nuevos provienen de secretos configurados y se hashean con Identity. Los hashes existentes no se modifican.
- El bootstrap nunca eleva usuarios existentes ni accesos reparados. Un acceso sin rol o usuario sin hash queda bloqueado hasta configuración administrativa explícita.

## Migraciones y datos anteriores

Se conservan los IDs y el historial de migraciones. `AddRolesPermisos` y `AddUsuarioPasswordHash` permiten información histórica incompleta en vez de fabricar privilegios o credenciales. `NormalizeCu01Initialization` también acepta bases que ya aplicaron las versiones anteriores: convierte roles compartidos según los accesos reales, conserva el ID original para una organización y copia los permisos efectivos a nuevas identidades para las otras.

Las definiciones compartidas sin pertenencia inequívoca se conservan en tablas de legado no expuestas por Application; no son editables ni asignables. La marca `EsPropietario` se conserva solo como compatibilidad de rollback, nunca autoriza. Si una versión anterior eliminó esa columna, no es posible recuperar su valor: se conserva como desconocido (`null`). Usuarios, accesos, envíos y bultos conservan sus identidades. Roles/hash históricos ausentes bloquean login, JWT y acceso demo.

El rollback no inventa datos requeridos ni fusiona permisos: si varios roles de organizaciones diferentes tienen el mismo código, se rechaza sin cambiar el esquema. Se requiere una decisión administrativa explícita para consolidarlos. Revertir más allá de la creación de las tablas de autenticación elimina esas columnas/tablas por definición; para volver a autenticar se requiere configurar roles/credenciales, no un seeder que eleve cuentas automáticamente.

## HTTP y autorización

`POST /api/auth/login` conserva su contrato JWT. La API valida firma/expiración e identidad/acceso actual contra PostgreSQL. Los permisos efectivos también se consultan allá, de modo que retirar un grant o cambiar un rol afecta inmediatamente tokens ya emitidos. Compatibilidad `X-NexoRuta-Acceso`: exclusivamente Development.

| Operación | Política |
| --- | --- |
| `GET/POST /api/roles`, `PUT/DELETE /api/roles/{id}` | `configuracion.gestionar` |
| `GET /api/roles/permisos`, `PUT /api/roles/{id}/permisos` | `configuracion.gestionar` |
| `PUT /api/roles/accesos/{accesoId}` (`RolId: null` retira asignación) | `usuarios.gestionar` |

Las operaciones siempre se limitan a la organización de la cuenta autenticada; no aceptan un propietario desde el request. Login y políticas de envíos permanecen vigentes. Tracking público no requiere credenciales y conserva sus respuestas.

## Verificación

Pruebas unitarias, de arquitectura y PostgreSQL temporal propio: bootstrap con 0/1/5 roles, nombres editados, hashes/asignaciones preservados, revocación, SQL de pertenencia inválida, conversión de esquema compartido ya aplicado, preservación de usuarios/accesos/envíos/bultos y rollback seguro. No se usa ni reinicializa la base de desarrollo `nexoruta`.

Las organizaciones ya existentes al aplicar la normalización se marcan como inicializadas: su configuración posterior debe administrarse explícitamente, sin volver a ejecutar plantillas iniciales sobre decisiones históricas.
