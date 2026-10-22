-- Cloud SQL administrators can manage these roles but cannot change the
-- superuser-only attributes, even when setting them to false. Fail closed.
DO $safe_runtime_roles$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles
        WHERE rolname IN ('nexoruta_api_runtime', 'nexoruta_worker_runtime', 'nexoruta_bootstrap_runtime')
          AND (rolsuper OR rolbypassrls OR rolreplication)) THEN
        RAISE EXCEPTION 'Unsafe runtime role attributes require repair by an authorized administrator.';
    END IF;
END $safe_runtime_roles$;

\getenv api_password NEXORUTA_API_DB_PASSWORD
\getenv worker_password NEXORUTA_WORKER_DB_PASSWORD
\getenv bootstrap_password NEXORUTA_BOOTSTRAP_DB_PASSWORD

SELECT format(
    'CREATE ROLE nexoruta_api_runtime LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD %L',
    :'api_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_api_runtime')
\gexec
SELECT format(
    'ALTER ROLE nexoruta_api_runtime LOGIN INHERIT NOCREATEDB NOCREATEROLE PASSWORD %L',
    :'api_password')
\gexec
GRANT nexoruta_api TO nexoruta_api_runtime;

SELECT format(
    'CREATE ROLE nexoruta_worker_runtime LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD %L',
    :'worker_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_worker_runtime')
\gexec
SELECT format(
    'ALTER ROLE nexoruta_worker_runtime LOGIN INHERIT NOCREATEDB NOCREATEROLE PASSWORD %L',
    :'worker_password')
\gexec
GRANT nexoruta_worker TO nexoruta_worker_runtime;

SELECT format(
    'CREATE ROLE nexoruta_bootstrap_runtime LOGIN INHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD %L',
    :'bootstrap_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexoruta_bootstrap_runtime')
\gexec
SELECT format(
    'ALTER ROLE nexoruta_bootstrap_runtime LOGIN INHERIT NOCREATEDB NOCREATEROLE PASSWORD %L',
    :'bootstrap_password')
\gexec

DO $membership_cleanup$
DECLARE membership record;
BEGIN
    FOR membership IN
        SELECT member_role.rolname AS member_name, granted_role.rolname AS granted_name
        FROM pg_auth_members m
        JOIN pg_roles member_role ON member_role.oid = m.member
        JOIN pg_roles granted_role ON granted_role.oid = m.roleid
        WHERE (member_role.rolname = 'nexoruta_api_runtime' AND granted_role.rolname <> 'nexoruta_api')
           OR (member_role.rolname = 'nexoruta_worker_runtime' AND granted_role.rolname <> 'nexoruta_worker')
           OR (member_role.rolname = 'nexoruta_bootstrap_runtime' AND granted_role.rolname <> 'nexoruta_bootstrap')
    LOOP
        EXECUTE format('REVOKE %I FROM %I', membership.granted_name, membership.member_name);
    END LOOP;
END
$membership_cleanup$;

DO $ownership_check$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relowner IN ('nexoruta_api_runtime'::regrole, 'nexoruta_worker_runtime'::regrole,
                             'nexoruta_bootstrap_runtime'::regrole)
          AND n.nspname IN ('public', 'nexoruta_app')
    ) OR EXISTS (
        SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE p.proowner IN ('nexoruta_api_runtime'::regrole, 'nexoruta_worker_runtime'::regrole,
                             'nexoruta_bootstrap_runtime'::regrole)
          AND n.nspname IN ('public', 'nexoruta_app')
    ) THEN
        RAISE EXCEPTION 'A runtime login owns application objects; repair ownership before provisioning.';
    END IF;
END
$ownership_check$;

REVOKE ALL ON SCHEMA public, nexoruta_app FROM nexoruta_api_runtime, nexoruta_worker_runtime, nexoruta_bootstrap_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM nexoruta_api_runtime, nexoruta_worker_runtime, nexoruta_bootstrap_runtime;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM nexoruta_api_runtime, nexoruta_worker_runtime, nexoruta_bootstrap_runtime;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA nexoruta_app FROM nexoruta_api_runtime, nexoruta_worker_runtime, nexoruta_bootstrap_runtime;
GRANT nexoruta_bootstrap TO nexoruta_bootstrap_runtime;

SELECT rolname, rolsuper, rolbypassrls, rolcreatedb, rolcreaterole
FROM pg_roles
WHERE rolname IN ('nexoruta_api_runtime', 'nexoruta_worker_runtime', 'nexoruta_bootstrap_runtime')
ORDER BY rolname;
