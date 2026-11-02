# Isolated PostgreSQL fixtures

Default: the existing owned Testcontainers PostgreSQL18/PostGIS3.6 container.

Linux without Docker: install PostgreSQL18/PostGIS via the superproject `scripts/cloud/install.sh`, then set `NEXORUTA_TEST_POSTGRES_MODE=native`. `NativePostgres` creates a private initdb cluster with generated credentials, loopback port, temporary data/socket directory and PostgreSQL18 identity checks. Under root, initdb/pg_ctl run as postgres. It stops and deletes only that generated test cluster. It never accepts an external connection string or uses the Cloud development database. All existing per-test databases, migrations, RLS logins and assertions remain the same. Unknown mode values fail closed.

Example: `NEXORUTA_TEST_POSTGRES_MODE=native dotnet test tests/NexoRuta.IntegrationTests --filter FullyQualifiedName~DestinosEnvioHttpTests`.

Run the authenticated Commerce full-stack browser probe in a separate dotnet-test invocation, as in the superproject check.sh. A combined HTTP+browser run in the Linux rehearsal returned401 during the browser login session; isolated invocations passed. That observation is a harness limitation, not evidence for weakening product authentication or disabling RLS. Native review remains pending.
