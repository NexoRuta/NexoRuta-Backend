# Release implementation

## Objective
Release main commits to the existing nexoruta-demo VPS contract without infrastructure changes.

## Constraints
No root/mobile changes, commits, remote tags, SSH or real deploy. Preserve develop fixes and PR CI. Explicit Demo:Enabled only; PostgreSQL remains untouched. Work-unit commits forbidden by user.

## Scope and acceptance
SHA/run-tagged GHCR images; immutable source tag; selective health-checked deploy with shared lock, monotonic execution state, exact env preservation and running-image rollback.

## Tasks
- [x] R1 Image and configuration compatibility, focused configuration tests.
- [x] R2 Main-only release workflow and safe VPS script, mock checks.
- [x] R3 Local static, .NET and Docker validation; report actual test counts and unavailable checks.
- [x] R4 Fix the authorized backend migration-test blocker minimally and validate exact Linux Backend CI commands plus 10 focused repetitions.

## Route
Delegated direct; preparation and multiple non-trivial files require bounded writer. Advisory forecast: approximately 650 authored lines per repository; no PR requested.

## Checks
Configuration RED/GREEN; bash mock scenarios; bash -n; actionlint; ShellCheck; three Docker builds; dotnet restore/build/test both solutions; git diff --check. Native risk assessment read-only, no review start.

## Progress
R1: Configuration RED observed: 2 failed / 3 passed, then GREEN 5/5. API image built and curl readiness succeeded in Production against local PostgreSQL 18. Demo user and configured operator account persisted after API restart.
R2: actionlint 1.7.12 passed; bash -n and ShellCheck 0.11.0 passed. 14/14 mocked deployment checks passed, including signal rollback, rollback failure, first-release honesty, watermark and real shared flock.
R3: restore/build succeeded (3 package-conflict warnings). Full suite ran 20 tests: 19 passed / 1 failed, zero skipped; ArchitectureTests contains zero tests. Rerun confirmed same result. Unmodified HEAD integration suite reproduces same failure (6/7 passed): Migracion_ConservaUsuarioEnvioYCuentaAlSepararLaEleccionDeOperador at line 223 compares account IDs. Isolated baseline test passed once; possible existing timing-sensitive ordering. At that stage no migration or test modification was authorized; subsequent R4 authorization and resolution follow below. git diff --check passed. Read-only native assessment returned high/unassessable because new files require explicit inventory; no review started.
Parent correction: failed env restoration or image rollback retains original env, immutable rollback override and non-secret recovery metadata; temporary registry credentials are removed even when recovery artifacts remain. Extended mock check observed RED before correction, then 14/14 checks passed per repository; ShellCheck and bash -n passed again. No rollback success is reported if env restore fails; partial prior-image availability is explicitly recorded as partial.
Commit: none by explicit user instruction.

## Next
Independent verification completed: final 14/14 deploy mocks in each repository and all four bash syntax/ShellCheck checks passed; no severe defect found. Existing backend migration-test CI blocker resolved by authorized R4; parent independent readback of all final diffs and new files completed. Native review awaits intended untracked inventory selection: selectorless status returned intended_untracked_selection_required; no review started and no approval issued. All three Docker images built and health endpoints verified locally; no remote operation performed.

## Authorized follow-up: R4
User now authorizes investigation and minimum fix for Migracion_ConservaUsuarioEnvioYCuentaAlSepararLaEleccionDeOperador. Allowed source edit is the integration test fixture only; production migration/seeder remain read-only unless root-cause evidence requires separate authorization. Delegated direct (preparation trigger). Migration Up retains minimum duplicate account UUID, while consecutive Guid.CreateVersion7 fixture calls have randomized same-millisecond ordering. Plan: observe failure under exact Linux CI commands, set deterministic ordered historical account UUIDs, preserve all behavior assertions, run full CI GREEN and 10 focused test repetitions. RED observed with exact Backend CI commands on the sixth full Linux run after five passing runs: 20 executed, 19 passed/1 failed. Expected and actual IDs shared the millisecond timestamp prefix but random subfields were reversed; migration correctly retained the lowest ID. Minimal fixture fix replaces two random UUIDv7 calls with ordered historical UUIDv7 constants and a ponytail comment; all assertions, production migration and seeder unchanged. GREEN: exact restore/build/test commands passed all 20 (8 unit + 12 integration, no skips; ArchitectureTests discovers zero tests), followed by 10/10 additional focused executions. Build retained 3 existing EF package-conflict warnings and zero errors. Local PostgreSQL18-alpine uses identical CI DB/user/password and NEXORUTA_TEST_POSTGRES connection; Linux test container shares PostgreSQL network namespace, so localhost:5432 is exact. Logs outside repo: TEMP/nexoruta-ci-fix-red.log plus red-2.log through red-5.log (five passing baseline runs), TEMP/nexoruta-ci-fix-red-repeat.log (full-suite RED), TEMP/nexoruta-ci-fix-green.log (20-case full suite plus 10 focused GREEN). Total R4 test executions: 120 pre-fix cases (five green suites and one red suite), 30 post-fix cases; no tests weakened/skipped/disabled. git diff --check passed. No commit, remote access, review start or deploy.

## Final audit
All 17 final files read: backend 9, frontend 8. No code blockers found. Independent actionlint, all four bash syntax/ShellCheck checks, 28 deploy mock scenarios and git diff --check passed. Backend exact Linux CI: 20/20 passed plus 10/10 focused migration-test repetitions. Native review remains pending intended untracked inventory selection; no review started or approval issued. No live deploy, remote operation or commit performed.
