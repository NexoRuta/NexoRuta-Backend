# Isolated tracking integration tests

Tracking tests provision PostgreSQL 18 automatically through Testcontainers 4.15.0.
They do not use Compose databases or `NEXORUTA_TEST_POSTGRES`.

## Run locally

Prerequisites: .NET 10 SDK and Docker Desktop running **Linux containers**. The
terminal must have permission to access Docker's named pipe on Windows. Docker
must be able to obtain `postgres:18-alpine` and Testcontainers' resource-reaper image.

From the backend repository root, in PowerShell:

```powershell
dotnet restore NexoRuta.sln
dotnet build NexoRuta.sln --no-restore
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento' --logger 'console;verbosity=normal'
# Repeat the same integration command to verify repeatability.
dotnet test tests/NexoRuta.IntegrationTests/NexoRuta.IntegrationTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento' --logger 'console;verbosity=normal'
dotnet test tests/NexoRuta.UnitTests/NexoRuta.UnitTests.csproj --no-build --no-restore --filter 'FullyQualifiedName~Seguimiento' --logger 'console;verbosity=normal'
```

The full integration suite is isolated from persistent PostgreSQL databases.
`EnvioPostgresRoundTripTests` obtains its connection string from
`PostgresFixture.CreateDatabaseAsync()` through `IntegrationDatabase.ConnectionString`;
it does not read an external connection setting. Its test database is created in
the fixture's owned Testcontainers PostgreSQL instance and disposed with it.

## Lifecycle and isolation

- xUnit v2 `IClassFixture<PostgresFixture>` starts one owned container per test
  class and disposes it after all selected tests, including when tests fail.
- The builder generates a username, cryptographic password and initial database
  name at runtime. Its built-in random host port mapping supplies the connection.
  No port bindings, host paths, operational credentials or volumes are configured.
- Each xUnit test instance calls `CreateDatabaseAsync` during `IAsyncLifetime`
  initialization: a new UUID-named database, all EF migrations and no pooling.
  Tests assert there are no shipments/links or pending migrations before arranging.
- Contexts, HTTP clients and factories are disposed with `using`/`await using`.
  Per-test databases live only until the shared container is disposed. There is
  no `EnsureDeleted`, reset, truncation or database deletion SQL in this harness.
- Testcontainers' resource reaper remains enabled for abnormal termination.
  Images may remain cached; the harness never prunes Docker or existing volumes.
- `IntegrationApiFactory` supplies host configuration **before** minimal-host
  startup reads it and replaces the context registration before startup migration
  and seeding. Telemetry is disabled; no global environment variables are modified.

## Security coverage

Seven independent tests cover a status-only 200 JSON response; unknown, expired
and revoked token 404 responses; no-store on every HTTP scenario; operator-scoped
queries; the composite PostgreSQL foreign key's SQLSTATE 23503; and SHA-256-only
storage verified from a fresh context and the complete stored row. Revocation
uses the domain method and EF persistence. These checks do **not** demonstrate RLS.

## Inspect cleanup

After the test process exits, these commands should show no resources from that
run (other concurrent Testcontainers sessions may own unrelated resources):

```powershell
docker ps -a --filter label=org.testcontainers=true
docker volume ls --filter label=org.testcontainers=true
docker network ls --filter label=org.testcontainers=true
```

No external-database fallback is implemented: PostgreSQL 18 with Testcontainers
was verified on Windows/Docker Desktop. Missing Docker access fails explicitly;
it never redirects tests to an existing database.

## Observed verification

On Windows/Docker Desktop with .NET SDK 10.0.401 and runtime/EF Core 10.0.12:
restore succeeded; final solution build had no warnings/errors; the final seven
integration tests passed twice consecutively; all eleven related unit cases passed.
The full Docker container/volume/network inventory matched before and after the
repetitions, no Testcontainers resources remained, and the existing PostgreSQL
containers' IDs, start timestamps and restart counts did not change.
