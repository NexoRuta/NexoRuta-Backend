# Isolated tracking integration tests

Tracking tests use a fixture-owned PostgreSQL 18 server. The default mode uses
Testcontainers; Linux also supports a private native PostgreSQL cluster.
RabbitMQ tests still need Docker for their broker. [PostgreSQL fixture modes](Fixtures/README.md)

## Run locally

Install the SDK pinned in [global.json](../../global.json) and configure Docker
to run Linux containers. On Windows, the terminal needs access to Docker.
The default PostgreSQL mode pulls `postgis/postgis:18-3.6-alpine`, and
Testcontainers needs its resource-reaper image.

From the backend repository root, in PowerShell:

```powershell
dotnet restore NexoRuta.sln --locked-mode
dotnet build NexoRuta.sln --no-restore
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento' --logger 'console;verbosity=normal'
# Repeat the same integration command to verify repeatability.
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento' --logger 'console;verbosity=normal'
dotnet test tests/NexoRuta.UnitTests/NexoRuta.UnitTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento' --logger 'console;verbosity=normal'
```

The full integration suite is isolated from persistent PostgreSQL databases.
`EnvioPostgresRoundTripTests` obtains its connection string from
`PostgresFixture.CreateDatabaseAsync()` through `IntegrationDatabase.ConnectionString`;
its database is created in the server owned by the fixture and removed when the
fixture disposes the server.

## Lifecycle and isolation

- xUnit v2 `IClassFixture<PostgresFixture>` starts a PostgreSQL environment for
  the test class and disposes it after all selected tests.
- The builder generates a username, cryptographic password and initial database
  name at runtime. Its built-in random host port mapping supplies the connection.
  No port bindings, host paths, operational credentials or volumes are configured.
- Each scenario that calls `CreateDatabaseAsync` gets a new UUID-named database
  with pooling disabled. The fixture verifies the server and database identity
  before migrations. Most scenarios migrate to the latest schema and migration
  scenarios can request a specific target.
- Contexts, HTTP clients and factories are disposed with `using`/`await using`.
  Per-test databases live until their fixture-owned server is disposed.
- Testcontainers' resource reaper remains enabled for abnormal termination.
  Images may remain cached; the harness never prunes Docker or existing volumes.
- `IntegrationApiFactory` supplies host configuration **before** minimal-host
  startup reads it and replaces the context registration before startup migration
  and seeding. Telemetry is disabled; no global environment variables are modified.

## Security coverage in the original tracking baseline

Seven independent tests cover a status-only 200 JSON response; unknown, expired
and revoked token 404 responses; no-store on every HTTP scenario; operator-scoped
queries; the composite PostgreSQL foreign key's SQLSTATE 23503; and SHA-256-only
storage verified from a fresh context and the complete stored row. Revocation
uses the domain method and EF persistence. Current PostgreSQL RLS coverage is
documented separately. [RLS policy tests](Seguridad/RlsPolicyTests.cs) ·
[HTTP flows with a restricted role](Seguridad/RlsRestrictedHttpFlowTests.cs)

## Inspect cleanup

After the test process exits, these commands should show no resources from that
run (other concurrent Testcontainers sessions may own unrelated resources):

```powershell
docker ps -a --filter label=org.testcontainers=true
docker volume ls --filter label=org.testcontainers=true
docker network ls --filter label=org.testcontainers=true
```

The fixture obtains its connection from the PostgreSQL instance it creates.
A startup failure is reported to the test runner.

## Historical verification

On Windows/Docker Desktop with .NET SDK 10.0.401 and runtime/EF Core 10.0.12:
restore succeeded; final solution build had no warnings/errors; the final seven
integration tests passed twice consecutively; all eleven related unit cases passed.
The full Docker container/volume/network inventory matched before and after the
repetitions, no Testcontainers resources remained, and the existing PostgreSQL
containers' IDs, start timestamps and restart counts did not change.
