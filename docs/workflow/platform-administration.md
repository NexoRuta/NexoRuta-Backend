# Provision organizations through platform administration

This backend/API slice adds independent platform authority and atomic operator/commerce provisioning. It does **not** complete CU-01. The separate Admin frontend is documented in `frontend/docs/admin.md` in the combined checkout.

## Authority boundary

| Scope | Stored authority | Available operations |
| --- | --- | --- |
| Platform | `Usuarios` + `AccesosPlataforma` + protected `RolesPlataforma` | Create/list/read operators and commerces |
| Operator | Existing `AccesosUsuario`, organization-owned roles and `CatalogoPermisos` | Existing Backoffice operations inside that operator |
| Commerce | Existing `AccesosUsuario`, organization-owned roles and `CatalogoPermisos` | Existing Commerce operations inside that commerce |

The platform capability is `plataforma.organizaciones.gestionar`, requiring both `RolesPlataforma.PuedeGestionarOrganizaciones` and its persisted `RolesPlataformaPermisos` grant. Authorization checks persisted capabilities, never role names/codes. This capability is registered in the separate `CatalogoPermisos.Plataforma`, deliberately absent from the organizational `CatalogoPermisos.Todos/Para` and cannot be granted through organization user/role APIs. Platform roles have no operator/commerce owner and cannot be assigned to organizational accesses.

The existing login endpoint and Identity password hasher are reused. Platform JWTs contain `nexoruta:ambito=plataforma`; organization JWTs retain their previous shape. Validation resolves platform access exclusively from its own table and never creates a `ContextoUsuario`. Cross-scope access IDs are rejected, even with an otherwise valid signature. Platform access state and `VersionSesion` are checked on every request; state transitions revoke old tokens. Removing the platform capability takes effect on the next request. Organization role/grant/state and self-protection rules are retained. Platform administration grants no operational shipment access.

`AccesosUsuario.UsuarioId` remains unique. There is no multi-access implementation and no fictitious organization. This slice deliberately initializes only one platform account; additional platform accounts and their administration require a separate authorized workflow, not a rerun of initialization.

## Configured local initialization

`DatosInicialesSeeder.SeedPlataformaAsync` runs independently of demo `SeedAsync`. `PlatformInitialization:Enabled` defaults to false. Compose maps the ignored local `.env` keys explicitly:

| Local key | API configuration |
| --- | --- |
| `NEXORUTA_PLATFORM_BOOTSTRAP=true` | `PlatformInitialization__Enabled` |
| `NEXORUTA_ADMIN_MAIL` | `PlatformInitialization__Email` |
| `NEXORUTA_ADMIN_PASSWORD` | `PlatformInitialization__Password` |

Provide credentials only through the ignored local environment/secret mechanism; never commit, print or put them in process arguments. Keep `NEXORUTA_SEED_DATA=false` and `NEXORUTA_APPLY_MIGRATIONS=false`. Only this local environment opts in; other environments remain disabled. No public bootstrap endpoint or prior login is needed.

Apply migrations explicitly before starting the updated API. From the backend directory, generate a bounded script from the database's actual last migration to `20261016045116_AddPlatformRolePermissions` with `dotnet ef migrations script <last-applied> 20261016045116_AddPlatformRolePermissions --project src/NexoRuta.Infrastructure --startup-project src/NexoRuta.Api --configuration Release`. Review it and feed it via stdin to `psql -X -v ON_ERROR_STOP=1` in the confirmed local Compose postgres service. Do not reset the database or delete volumes. Habitual startup never applies migrations.

The shared use case hashes using the existing project hasher and creates identity/access/protected role/catalog/grants in one transaction. A transaction-scoped table lock serializes singleton initialization; unique normalized email/access constraints arbitrate competing writers. A correctly configured existing account is a no-op even if the configured password changes: the stored password is never replaced. Wrong identity, organizational email, disabled/incompatible platform authority or missing configuration fails safely without repair/elevation or credential logging. The new additive grant migration backfills only roles previously enabled, preserving existing IDs, passwords and revocation state.

## Interactive alternative (not required for local configured startup)

This remains an explicit administrative alternative, distinct from demonstration seeders.

1. Review the destination and take the normal deployment backup. Apply the pending migration separately through the approved deployment procedure.
2. Configure `ConnectionStrings__Postgres` through the deployment secret mechanism for that explicitly selected destination. Do not put connection secrets or account passwords in shell commands, process arguments, source, logs, tickets or history.
3. From the backend directory, in an interactive terminal, run:

   ```powershell
   dotnet run --project src/NexoRuta.Api --configuration Release --no-launch-profile -- platform-admin create
   ```

   Or run the published API:

   ```powershell
   dotnet NexoRuta.Api.dll platform-admin create
   ```

4. Enter the email, password and password confirmation at the prompts. Password input is hidden; the shared rule requires a valid normalized email and a password of at least 12 characters that is not entirely whitespace.
5. Exit `0` means account/role/access committed together. Exit `1` means initialization failed or was refused. Exit `2` means invalid invocation, redirected input, or confirmation mismatch. No session/JWT is returned.

The command rejects extra arguments and redirected credential input, requires no JWT key, opens no HTTP listener, and never invokes automatic migrations or `DatosInicialesSeeder`, even when their configuration switches are enabled. Existing email (including case/whitespace variants) or completed initialization is a refusal: no password replacement, elevation, additional access or repair. A singleton database constraint also arbitrates concurrent first-account attempts. Loss of the initial account is **not** repaired automatically; recovery/reset and global user management are out of scope.

## API contract

Authenticate through `POST /api/auth/login` using the platform account. Send its Bearer token over HTTPS. Organization creation never logs in or impersonates the new administrator.

| Method | Route | Result |
| --- | --- | --- |
| POST | `/api/admin/operadores` | `201`, organization + administrator user/access/role IDs; Location points to detail |
| POST | `/api/admin/comercios` | Same contract for an independent commerce |
| GET | `/api/admin/operadores?pagina=1&tamanoPagina=20` | Paginated operators |
| GET | `/api/admin/comercios?pagina=1&tamanoPagina=20` | Paginated commerces |
| GET | `/api/admin/operadores/{id}` | Organization ID and name, or `404` |
| GET | `/api/admin/comercios/{id}` | Organization ID and name, or `404` |

All six operations require the platform capability: anonymous callers receive `401`; organization accounts receive `403`. Lists return `{ items, pagina, tamanoPagina, total }`, default page 1/size 20, size 1–100, ordered by name then UUID (including organizations with identical names). Invalid bounds/overflow receive `400`. These routes are separate from the unchanged Commerce operator catalog (`/api/comercio/operadores`, compatibility alias `/api/operadores`).

Both POST routes use this shape (illustrative values only; replace the password placeholder with a securely supplied password):

```json
{
  "nombre": "Example organization",
  "administrador": {
    "email": "administrator@example.invalid",
    "password": "<securely supplied password, at least 12 characters>"
  }
}
```

```json
{
  "organizacion": { "id": "00000000-0000-0000-0000-000000000001", "nombre": "Example organization" },
  "usuarioId": "00000000-0000-0000-0000-000000000002",
  "accesoId": "00000000-0000-0000-0000-000000000003",
  "rolId": "00000000-0000-0000-0000-000000000004"
}
```

Only organization name is required by the current model/documentation (maximum 160, trimmed, not unique). No tax/address fields or commerce/operator ownership link are invented. Email normalization, validation, global uniqueness, password validation and hashing reuse the existing account rules. Unknown provisioning fields are rejected (`400`), rather than accepting client-selected roles, grants, IDs or platform authority. Duplicate email yields `409`, including concurrent requests and duplicates across organization types. Existing identities are never reused or modified. Responses contain no credentials, hashes or administrator JWTs.

Each new organization receives a protected administrator role and **exactly** `CatalogoPermisos.Para(scope)`. Organization, role, catalog entries/grants, identity and active access commit in one PostgreSQL transaction. Catalog entries are inserted only if missing with `ON CONFLICT`; existing grants are not rewritten. Newly provisioned organizations are marked initialized so demo initialization cannot install templates later. Administrators cannot rename/delete/change their own or protected roles, change their own access state or assign their own access a different role.

| Initial administrator | Permissions from the current catalog |
| --- | --- |
| Operator | `usuarios.gestionar`, `configuracion.gestionar`, `envios.consultar`, `envios.gestionar` |
| Commerce | `usuarios.gestionar`, `configuracion.gestionar`, `envios.crear`, `envios.consultar` |

## Migration and deployment

`20261016035056_AddPlatformAdministration` adds only platform role/access tables and their indexes/FKs/CHECKs, plus the expression index `IX_Usuarios_EmailNormalizado` on `lower(btrim("Email"))`. The expression index is intentionally maintained as migration SQL (EF has no expression-index declaration here). The old exact email unique index and the single organizational-access index remain intact. Historical migrations are unchanged, and no data is updated or deleted.

If historical identities collide after email normalization, migration fails transactionally rather than silently merging, deleting or elevating them. Resolve that conflict through an explicitly approved administrative decision before retrying; this slice supplies no destructive cleanup. Applying migrations is a separate deployment operation. Both platform migrations were explicitly applied to the authorized local Compose database on 2026-10-16; other environments still require their own authorized deployment. Existing rows were compared before/after without printing hashes; a second configured startup preserved the platform account unchanged.

## Verification and remaining work

Tests use only fixture-owned PostgreSQL 18 Testcontainers. They cover authorization and scope manipulation, full permission sets, protected roles and self-protection, login and shipment isolation, duplicate/concurrent creation, rollback after saving every entity, bootstrap conflict/concurrency, CLI safety and upgrade from the previous schema with existing identities/roles/grants/shipments.

```powershell
dotnet build NexoRuta.sln --configuration Release
dotnet test tests/NexoRuta.UnitTests --configuration Release --no-build
dotnet test tests/NexoRuta.IntegrationTests --configuration Release --no-build
dotnet test tests/NexoRuta.ArchitectureTests --configuration Release --no-build
dotnet ef migrations has-pending-model-changes --project src/NexoRuta.Infrastructure --startup-project src/NexoRuta.Api --configuration Release --no-build
git diff --check
```

The independent Admin frontend now consumes the administrative routes and the existing self-context/self-permissions reads. Those two reads expose `tipo: "Plataforma"`, null organizational/role fields and the current platform capability without creating an organizational `ContextoUsuario`. Existing organization responses retain their values; their portals reject the platform type at login. Operational endpoints remain forbidden to platform tokens. Admin cannot bootstrap through HTTP. OpenAPI is published through the existing Development endpoint and mirrored in `frontend/contracts/nexoruta.openapi.json`; see `frontend/docs/admin.md` in the combined checkout.

Still pending: complete Identity/OIDC migration; RLS as an additional tenant boundary; organization deletion, impersonation, password recovery/reset, additional platform accounts and global user management. Commerce continues selecting an available operator per shipment, with no prior commerce/operator management relationship.

## Diagnosing an outdated command image

The reported `docker compose exec api dotnet NexoRuta.Api.dll platform-admin create` port-8080 collision was traced to an outdated running image: its API assembly lacked the `platform-admin` dispatcher marker, while the current Release assembly contained it. Current `Program.cs` dispatches before server startup; no dispatcher defect was demonstrated. Rebuild/recreate the API before using the interactive alternative (`exec -it`, not redirected input). An invalid verb exits 2 with usage and no Kestrel listener. Normal local setup now uses the independent configured bootstrap, not that command.
