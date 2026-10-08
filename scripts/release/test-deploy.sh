#!/usr/bin/env bash
set -Eeuo pipefail
script=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/deploy.sh
component=backend
services=(api)
keys=(NEXORUTA_API_TAG)
tmp=$(mktemp -d)
trap 'rm -rf -- "$tmp"' EXIT
mkdir "$tmp/bin"
real_cp=$(command -v cp)
export REAL_CP="$real_cp"
export MOCK_ROOT="$tmp"
export PATH="$tmp/bin:$PATH"
cat > "$tmp/bin/cp" <<'MOCK'
#!/usr/bin/env bash
if [[ ${MOCK_FAIL:-} == env_restore && ${*: -1} == */env.restore ]]; then exit 35; fi
exec "$REAL_CP" "$@"
MOCK
chmod +x "$tmp/bin/cp"
cat > "$tmp/bin/docker" <<'MOCK'
#!/usr/bin/env bash
set -euo pipefail
printf '%s\n' "$*" >> "$MOCK_ROOT/calls"
if [[ $1 == login ]]; then
    cat >/dev/null
    printf credential > "$DOCKER_CONFIG/config.json"
    exit 0
fi
if [[ $1 == inspect ]]; then
    service=${4#container-}
    case $3 in
        '{{.Image}}') printf 'sha256:%064d\n' 1 ;;
        *State.Status*) if [[ ${MOCK_FAIL:-} == health ]]; then echo 'running unhealthy'; else echo 'running healthy'; fi ;;
        '{{.Config.Image}}') cat "$MOCK_ROOT/current-$service" ;;
        *) exit 22 ;;
    esac
    exit
fi
[[ $1 == compose ]] || exit 23
shift
override=
while [[ $1 == --* || $1 == -f ]]; do
    if [[ $1 == -f && $2 == */rollback.yaml ]]; then override=$2; fi
    shift 2
done
command=$1; shift
case $command in
    ps)
        service=${*: -1}
        if [[ -f "$MOCK_ROOT/current-$service" ]]; then echo "container-$service"; fi
        ;;
    pull)
        [[ ${MOCK_FAIL:-} != pull ]] || exit 31
        ;;
    up)
        if [[ -z $override && ( ${MOCK_FAIL:-} == up || ${MOCK_FAIL:-} == rollback || ${MOCK_FAIL:-} == env_restore ) ]]; then exit 32; fi
        if [[ -z $override && ${MOCK_FAIL:-} == signal ]]; then kill -TERM "$PPID"; exit 33; fi
        if [[ -n $override && ${MOCK_FAIL:-} == rollback ]]; then exit 34; fi
        while [[ ${1:-} == -* ]]; do
            if [[ $1 == --wait-timeout ]]; then shift 2; else shift; fi
        done
        for service in "$@"; do
            if [[ -n $override ]]; then
                printf 'sha256:%064d\n' 1 > "$MOCK_ROOT/current-$service"
            else
                key="NEXORUTA_${service^^}_TAG"
                tag=$(awk -F= -v key="$key" '$1==key { print $2 }' .env)
                echo "ghcr.io/nexoruta/nexoruta-$service:$tag" > "$MOCK_ROOT/current-$service"
            fi
        done
        ;;
    *) exit 24 ;;
esac
MOCK
chmod +x "$tmp/bin/docker"
sha=1111111111111111111111111111111111111111
reset() {
    rm -f "$tmp"/current-* "$tmp/calls"
    rm -rf "$tmp/deploy"
    mkdir "$tmp/deploy"
    : > "$tmp/deploy/compose.vps.yaml"
    printf '%s\n' '# preserve me' 'NEXORUTA_POSTGRES_PASSWORD=literal$ with spaces # !' \
        'NEXORUTA_API_TAG=placeholder-api' 'NEXORUTA_COMMERCE_TAG=placeholder-commerce' \
        'NEXORUTA_BACKOFFICE_TAG=placeholder-backoffice' > "$tmp/deploy/.env"
    cp "$tmp/deploy/.env" "$tmp/original.env"
    for service in "${services[@]}"; do echo "old-$service" > "$tmp/current-$service"; done
}
deploy() {
    local run=${1:-10} attempt=${2:-1} source=${3:-$sha}
    NEXORUTA_PULL_TOKEN=test-token bash "$script" "$tmp/deploy" "$source" "$run" "$attempt" 100 \
        "sha-$source-run-100-attempt-$attempt" pull-user > "$tmp/output" 2>&1
}
check_count=0
pass() { check_count=$((check_count + 1)); echo "ok $check_count - $1"; }
reset
deploy
grep -q 'result=healthy' "$tmp/deploy/.release-$component.log"
for key in "${keys[@]}"; do grep -q "^$key=sha-$sha-run-100-attempt-1$" "$tmp/deploy/.env"; done
grep -Fxq 'NEXORUTA_POSTGRES_PASSWORD=literal$ with spaces # !' "$tmp/deploy/.env"
for key in NEXORUTA_API_TAG NEXORUTA_COMMERCE_TAG NEXORUTA_BACKOFFICE_TAG; do
    if [[ " ${keys[*]} " != *" $key "* ]]; then
        grep "^$key=" "$tmp/original.env" | grep -Fx -f - "$tmp/deploy/.env" >/dev/null
    fi
done
pass 'healthy release updates only owned tags and preserves password/sibling values'
if grep -E '(pull|up).* (postgres|foreign)|down|prune' "$tmp/calls"; then exit 1; fi
grep -q 'up -d --no-deps --wait --wait-timeout 180' "$tmp/calls"
pass 'only owned services pulled/updated, health wait and no-deps mandatory'
deploy
pass 'identical execution is idempotent'
if deploy 9; then exit 1; fi
grep -q 'Stale release rejected' "$tmp/output"
pass 'older execution rejected'
if deploy 10 1 2222222222222222222222222222222222222222; then exit 1; fi
grep -q 'Execution identity collision' "$tmp/output"
pass 'same execution cannot change SHA'
deploy 10 2
if deploy 10 1; then exit 1; fi
pass 'older attempt rejected'
for failure in pull up health signal; do
    reset
    if MOCK_FAIL="$failure" deploy; then echo "Expected failure: $failure"; exit 1; fi
    cmp "$tmp/original.env" "$tmp/deploy/.env"
    grep -q 'result=failed_rolled_back' "$tmp/deploy/.release-$component.log"
    for service in "${services[@]}"; do grep -q '^sha256:' "$tmp/current-$service"; done
    if deploy 9; then exit 1; fi
    pass "$failure failure restores exact env and previous immutable image; watermark retained"
done
reset
rm -f "$tmp"/current-*
if MOCK_FAIL=up deploy; then exit 1; fi
cmp "$tmp/original.env" "$tmp/deploy/.env"
grep -q 'result=failed_no_previous_images' "$tmp/deploy/.release-$component.log"
pass 'first release failure honestly reports no prior containers'
reset
if MOCK_FAIL=rollback deploy; then exit 1; fi
cmp "$tmp/original.env" "$tmp/deploy/.env"
grep -q 'result=failed_rollback_failed' "$tmp/deploy/.release-$component.log"
grep -q 'manual recovery required' "$tmp/output"
recovery=$(find "$tmp/deploy" -maxdepth 1 -type d -name ".release-$component.*")
[[ -n $recovery && -f $recovery/rollback.yaml && -f $recovery/recovery.txt && ! -e $recovery/docker ]]
cmp "$tmp/original.env" "$recovery/env.before"
pass 'rollback failure retains recovery files without registry credentials'
reset
if MOCK_FAIL=env_restore deploy; then exit 1; fi
grep -q 'result=failed_env_restore' "$tmp/deploy/.release-$component.log"
if grep -q 'result=failed_rolled_back' "$tmp/deploy/.release-$component.log"; then exit 1; fi
recovery=$(find "$tmp/deploy" -maxdepth 1 -type d -name ".release-$component.*")
[[ -n $recovery && -f $recovery/rollback.yaml && -f $recovery/recovery.txt && ! -e $recovery/docker ]]
cmp "$tmp/original.env" "$recovery/env.before"
if grep -q 'rollback.yaml up' "$tmp/calls"; then exit 1; fi
pass 'env restoration failure preserves original bytes and metadata, never claims rollback'
reset
# Hold the actual common lock: neither repository may mutate before acquiring it.
(
    flock -x 9
    : > "$tmp/locked"
    sleep 2
) 9>"$tmp/deploy/.release.lock" &
holder=$!
while [[ ! -f "$tmp/locked" ]]; do sleep 0.05; done
deploy &
pending=$!
sleep 0.3
[[ ! -f "$tmp/deploy/.release-$component.state" ]]
wait "$holder"
wait "$pending"
pass 'shared .release.lock serializes before watermark or image changes'
echo "PASS: $check_count deploy checks ($component)"

