# Shipment boundary refactor

Application results no longer define the HTTP response. The API explicitly flattens
`OrigenEnvio` and package data into the existing public shipment schemas. Operator
remains the tenant and commerce remains global. No domain entity, EF model,
migration, seeder, FK, or transaction boundary changed.

## Trace and responsibilities

| Boundary | Type / location | Responsibility |
| --- | --- | --- |
| Commerce form | frontend `Home.razor`, `CrearEnvioRequest` | Editable input and DataAnnotations; reuse the request rather than add a duplicate form DTO. |
| HTTP client | frontend `EnviosApiClient` | JSON, access header, errors, cancellation. Catalog uses the existing API route `/api/comercio/operadores`. |
| API input | API `Contracts/Envios/CrearEnvioRequest` | Validate HTTP input independently of Application and Domain. |
| Command | Application `CrearEnvioCommand` | Intent/input only; named arguments distinguish operator, recipient, address and measurements. |
| Coordination | Application `CrearEnvioUseCase` | Resolve current commerce and operator, construct entities, save once, return result. |
| Context | `ContextoUsuario.RequerirComercio()` | Return non-null/nonempty commerce ID and name, or fail before persistence. An operator access still fails with the existing forbidden exception. |
| Domain | `Envio`, `Bulto`, `Destinatario`, `Direccion`, `AccesoUsuario` | Keep identities and invariants. No cosmetic value objects or copied construction factory. |
| Persistence | `EfEnviosRepository` | Preserve queries, tenant filters and one atomic `SaveChangesAsync`; named result construction. |
| Application output | `EnvioCreado`, `EnvioDetalle` | Share `OrigenEnvio` for tenant/commerce/creator provenance and `BultoDetalle` for package data. No transport annotations. |
| API output | API `Contracts/Envios/EnvioResponses` | Explicitly flatten Application results; own the published `EnvioCreado`, `EnvioDetalle`, `BultoDetalle` schema names. |
| Backoffice | frontend `EnvioResponse`, `BultoResponse`, `IndexModel` | Deserialize independent HTTP contracts and render operator-scoped shipments. |

The former 12-argument creation result duplicated the package fields and mixed
shipment identity with provenance. The former 11-argument listing result duplicated
that provenance. One shared grouping removes that duplication; named arguments
remove positional ID/decimal ambiguity. API response DTOs are necessary public
boundaries, not intermediate copies introduced between identical DTOs.

`AccesoUsuario` keeps its validated constructor because changing it would require
touching the explicitly frozen seeder. Its mutually exclusive organizations,
nonempty IDs and commerce-only ownership remain domain invariants. Tests explicitly
distinguish access IDs from a shared person ID. No uniqueness invariant was added
to the domain.

## Compatibility evidence and intentional correction

- Create/list JSON property names, types, response schema names, status codes and
  Location remain unchanged. No server route changed.
- Decimal annotations now use `ParseLimitsInInvariantCulture = true`. The baseline
  live OpenAPI incorrectly emitted minimum `1`; the only four schema changes are
  those minima becoming `0.01`, matching the already-published frontend bounds.
  Claiming byte-for-byte OpenAPI equality would be incorrect.
- A pre-existing cross-repository mismatch made Commerce request `/api/operadores`
  while backend published `/api/comercio/operadores`. The client, snapshot and route
  checks now match the existing backend API, without changing operator eligibility.
- Frontend xUnit runs the entire suite under `es-UY`; limit tests assert the actual
  culture. Backend checks both `es-UY` and `en-US` with all four numeric properties.
- PostgreSQL rollback test proves an invalid tenant FK leaves no shipment, package,
  recipient or address. Architecture tests enforce inner-layer independence and
  API-owned shipment responses rather than retesting individual mapping assignments.

Exact commands, counts, failures and HTTP evidence are recorded in
`odd/tasks/refactor-shipment-boundaries.md` (locally ignored, not committed).

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
4. Replace demo account selection in `AccesoSeleccionadoHandler` / `UsuarioActualHttp`
   and frontend login with verified Identity membership. Never trust the submitted
   access header or an operator ID as authorization.
5. Apply contextual role policies at create/list and catalog boundaries. Validate
   tenant/commerce scope again before persistence and reads; retain composite FKs
   and atomicity. RLS design/credentials/session isolation remain a separate change.

None of Identity, roles, new policies, RLS, relationship models, migrations or
authorization behavior is implemented by this refactor.
