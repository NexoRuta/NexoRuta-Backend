# Backend refactor: actual branch scope

`feature/refactor-envios` targets `develop` according to the local branch history.
It includes structural changes **and** changes to internal contracts, validation,
bootstrap and runtime dependencies. It is not a file-move-only refactor.

## Structural work: REF-01 to REF-04

- One main public Application type per file.
- Administration and shipment files grouped by functional module and responsibility.
- Namespaces aligned with folders and consumers updated.
- Existing types retained without duplicate declarations.

## Pending local Feature-First work: ARQ-01 and ARQ-02

API controllers now belong to `Features/<feature>/Controllers`. Shipment HTTP
contracts are separated under `Features/Envios/Contracts/Requests` and
`Features/Envios/Contracts/Responses`; security and health checks live in `Core`.
Infrastructure repositories live in `Administracion/Repositories` and
`Envios/Repositories`. Shared persistence, bootstrap and migrations retain their
existing responsibilities and locations.

These local changes preserve implementation bodies, routing and JSON properties.
Compared with the pre-ARQ-01 API, only the order of the root OpenAPI `tags` array
changed. This does not undo the intentional decimal-validation changes already
included earlier in the branch. No code forces tag ordering and no contract
snapshot was updated to conceal a difference.

Frontend consumers now use specialized shipment, access, operator and user
clients, with shared HTTP handling in `Core/Http/ApiHttp`; the shipment component
is `Components/Features/Envios/Pages/CrearEnvio.razor`.

## Additional work already present in the branch

| Area | Actual change |
| --- | --- |
| Application results | `EnvioCreado` now groups `Id`, `OrigenEnvio` and `BultoDetalle`; `EnvioDetalle` groups provenance in `OrigenEnvio` and retains its package list. These are changed constructors and property shapes, not just file moves. |
| HTTP boundary | API-owned response records explicitly flatten Application results into the existing JSON property names and types. |
| Context validation | `ContextoUsuario.RequerirComercio()` checks the commerce ID and name before shipment persistence; tests cover incomplete contexts and invalid IDs. |
| Decimal validation | `ParseLimitsInInvariantCulture = true` makes all four decimal bounds independent of process culture. |
| REF-05 bootstrap | `AccesoInicial` and `DatosInicialesSeeder` remain; configuration is explicit and required. Demo flag branches and historical name-based transformations were removed without deleting existing records. |
| EST-03 runtime | Infrastructure explicitly references `Microsoft.EntityFrameworkCore.Relational` 10.0.12; Worker resolves the same EF runtime version. Npgsql provider 10.0.3 is unchanged. |

Internal Application contracts changed; **source or binary compatibility with
their former constructors and properties is not promised**. JSON compatibility
at the existing shipment endpoints is a separate boundary, preserved by explicit
mapping. Decimal validation is an intentional behavior correction, so byte-for-byte
OpenAPI compatibility is not claimed.

During the original shipment boundary refactor, the seeder was left unchanged.
REF-05 subsequently changed its configuration and removed legacy Demo conversion.
Across the accumulated backend changes, Domain entities, EF model, migrations,
FKs, transaction boundaries and server routes remain unchanged. Operator remains
the tenant and commerce remains global.

The existing backend commits `269a74f` and `59a60b8` contain these accumulated
changes. EST-03 adds the runtime reference; EST-05 documents their actual scope
without rewriting history. Frontend changes are separate repository work and
must not be silently included in the backend PR.

## Trace and responsibilities

| Boundary | Type / location | Responsibility |
| --- | --- | --- |
| Commerce form | frontend `Components/Features/Envios/Pages/CrearEnvio.razor`, `CrearEnvioRequest` | Editable input and DataAnnotations; reuse the request rather than add a duplicate form DTO. |
| HTTP clients | frontend `EnviosApiClient`, `AccesosApiClient`, `OperadoresApiClient`, `UsuariosApiClient`; shared `ApiHttp` | Responsibilities are separated by feature; shared JSON, errors and cancellation are preserved. Access listing sends no access header; catalog retains `/api/comercio/operadores`. |
| API input | `src/NexoRuta.Api/Features/Envios/Contracts/Requests/CrearEnvioRequest.cs` | Validate HTTP input independently of Application and Domain. |
| Command | Application `CrearEnvioCommand` | Intent/input only; named arguments distinguish operator, recipient, address and measurements. |
| Coordination | Application `CrearEnvioUseCase` | Resolve current commerce and operator, construct entities, save once, return result. |
| Context | `ContextoUsuario.RequerirComercio()` | Return non-null/nonempty commerce ID and name, or fail before persistence. An operator access still fails with the existing forbidden exception. |
| Domain | `Envio`, `Bulto`, `Destinatario`, `Direccion`, `AccesoUsuario` | Keep identities and invariants. No cosmetic value objects or copied construction factory. |
| Persistence | `EfEnviosRepository` | Preserve queries, tenant filters and one atomic `SaveChangesAsync`; named result construction. |
| Application output | `EnvioCreado`, `EnvioDetalle` | Share `OrigenEnvio` for tenant/commerce/creator provenance and `BultoDetalle` for package data. No transport annotations. |
| API output | `src/NexoRuta.Api/Features/Envios/Contracts/Responses/` (`EnvioCreado.cs`, `EnvioDetalle.cs`, `BultoDetalle.cs`) | Explicitly flatten Application results; own the published `EnvioCreado`, `EnvioDetalle`, `BultoDetalle` schema names. |
| Backoffice | frontend `EnvioResponse`, `BultoResponse`, `IndexModel` | Deserialize independent HTTP contracts and render operator-scoped shipments. |

The former 12-argument creation result duplicated the package fields and mixed
shipment identity with provenance. The former 11-argument listing result duplicated
that provenance. One shared grouping removes that duplication; named arguments
remove positional ID/decimal ambiguity. API response DTOs are necessary public
boundaries, not intermediate copies introduced between identical DTOs.

`AccesoUsuario` keeps its existing validated constructor. The original boundary
refactor did not change it or the seeder; REF-05 later changed bootstrap only.
Its mutually exclusive organizations,
nonempty IDs and commerce-only ownership remain domain invariants. Tests explicitly
distinguish access IDs from a shared person ID. No uniqueness invariant was added
to the domain.

## Compatibility evidence and intentional correction

- Create/list JSON property names, types, response schema names, status codes and
  Location remain unchanged. No server route changed.
- Decimal annotations now use `ParseLimitsInInvariantCulture = true`. The
  intended lower bound is `0.01`, regardless of process culture; backend tests
  check all four properties under `es-UY` and `en-US`. This can change generated
  OpenAPI numeric bounds. Claiming byte-for-byte OpenAPI equality would be incorrect.
- A pre-existing cross-repository mismatch made Commerce request `/api/operadores`
  while backend published `/api/comercio/operadores`. The client, snapshot and route
  checks now match the existing backend API, without changing operator eligibility.
- Frontend xUnit runs the entire suite under `es-UY`; limit tests assert the actual
  culture. Backend checks both `es-UY` and `en-US` with all four numeric properties.
- PostgreSQL rollback test proves an invalid tenant FK leaves no shipment, package,
  recipient or address. Architecture tests enforce inner-layer independence and
  API-owned shipment responses rather than retesting individual mapping assignments.

## Reproducible checks and retained baseline

The versioned [backend README](../../README.md#pruebas-postgresql-destino-aislado-obligatorio)
contains the commands, PostgreSQL isolation requirements and the retained EST-04
baseline: restore succeeded; build had zero errors and warnings; unit tests 19/19,
architecture tests 3/3 and integration tests 42/42. Configuration/DI tests 17/17
are included in integration and were also run separately; do not add them twice.

PostgreSQL scenarios use temporary `nexoruta_test_<UUIDv7>` databases and call
`EnsureDeletedAsync()` during disposal. Set `NEXORUTA_TEST_POSTGRES` only for the
test process, verify the isolated PostgreSQL 18 identity before running, and
never use a shared persistent instance. The current fallback targets loopback
port 5432 without an isolation check; on Windows it may reach another server.
EST-02 to EST-04 used exclusive temporary PostgreSQL 18 containers.

EST-05 changes documentation only and did not rerun technical suites. These
results are local historical evidence, not remote CI proof. No ignored local
task file is required to understand the scope or reproduce the checks.

## Separate functional task: D01 eligibility and contextual authorization

The current implementation lists registered operators and validates existence only.
This observable behavior is preserved **temporarily**, not adopted as definitive
business policy. Existing CU-01/CU-07 wording describes that implementation snapshot;
it does not resolve D01's commerce-as-client-of-operator requirement. Existing tests
are retained, but no new arbitrary-operator eligibility requirement was added.

Before implementing the next functional change:

1. Resolve D01's commerce-operator client relationship: who establishes it, its
   lifecycle/eligibility, and how a global commerce works with multiple operators.
2. Specify permitted operations for a person's selected access and contextual role;
   an access is not the person. Reconcile the current EF unique `UsuarioId` index
   with multiple accesses per person in that separate schema/migration task.
3. At the operator catalog and before creation, enforce the approved eligibility
   rule. Add allow/deny, inactive/revoked relationship and multi-operator tests only
   after that rule is approved. Do not infer membership from an existing shipment.
4. Replace temporary account selection in `AccesoSeleccionadoHandler` / `UsuarioActualHttp`
   and frontend login with verified Identity membership. Never trust the submitted
   access header or an operator ID as authorization.
5. Apply contextual role policies at create/list and catalog boundaries. Validate
   tenant/commerce scope again before persistence and reads; retain composite FKs
   and atomicity. RLS design/credentials/session isolation remain a separate change.

None of Identity, roles, new policies, RLS, relationship models, migrations or
authorization behavior is implemented by this refactor.
