# Isolated PostgreSQL fixtures

Default (`NEXORUTA_TEST_POSTGRES_MODE=docker` or unset): a fixture-owned
Testcontainers server using `postgis/postgis:18-3.6-alpine`.

Linux without Docker: install PostgreSQL18/PostGIS via the superproject `scripts/cloud/install.sh`, then set `NEXORUTA_TEST_POSTGRES_MODE=native`. `NativePostgres` creates a private initdb cluster with generated credentials, loopback port, temporary data/socket directory and PostgreSQL18 identity checks. Under root, initdb/pg_ctl run as postgres. It stops and deletes only that generated test cluster. It never accepts an external connection string or uses the Cloud development database. All existing per-test databases, migrations, RLS logins and assertions remain the same. Unknown mode values fail closed.

Example: `NEXORUTA_TEST_POSTGRES_MODE=native dotnet test tests/NexoRuta.IntegrationTests --filter FullyQualifiedName~DestinosEnvioHttpTests`.

When reproducing the native Linux rehearsal, run the authenticated Commerce
full-stack browser probe in a separate `dotnet test` invocation. In that
rehearsal, a combined HTTP and browser run returned `401` during browser login.
Isolated invocations passed.
