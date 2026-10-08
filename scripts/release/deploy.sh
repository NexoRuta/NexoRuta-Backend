#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

# Invoked through bash -s; GHCR credentials arrive only on stdin-generated environment.
component=backend
deploy_dir=${1:?deploy directory required}
sha=${2:?source SHA required}
run_number=${3:?run number required}
attempt=${4:?run attempt required}
run_id=${5:?run id required}
tag=${6:?image tag required}
pull_user=${7:?GHCR pull user required}
[[ $deploy_dir == /* && $sha =~ ^[0-9a-f]{40}$ && $run_number =~ ^[1-9][0-9]{0,14}$ &&
   $attempt =~ ^[1-9][0-9]{0,8}$ && $run_id =~ ^[1-9][0-9]{0,19}$ &&
   $tag == "sha-$sha-run-$run_id-attempt-$attempt" && $pull_user =~ ^[A-Za-z0-9_-]+$ ]] ||
    { echo "Invalid release arguments" >&2; exit 2; }
cd -- "$deploy_dir"
[[ -f compose.vps.yaml && -f .env ]] || { echo "Missing VPS compose or .env" >&2; exit 2; }
exec 9>.release.lock
flock -x 9

services=(api)
keys=(NEXORUTA_API_TAG)
unset "${keys[@]}"
images=(ghcr.io/nexoruta/nexoruta-api)
state=".release-$component.state"
journal=".release-$component.log"
if [[ -f $state ]]; then
    read -r previous_run previous_attempt previous_sha previous_tag < "$state"
    [[ $previous_run =~ ^[1-9][0-9]{0,14}$ && $previous_attempt =~ ^[1-9][0-9]{0,8}$ ]] ||
        { echo "Invalid release high-watermark" >&2; exit 2; }
    if (( run_number < previous_run || (run_number == previous_run && attempt < previous_attempt) )); then
        echo "Stale release rejected" >&2
        exit 3
    fi
    if (( run_number == previous_run && attempt == previous_attempt )) &&
       [[ $sha != "$previous_sha" || $tag != "$previous_tag" ]]; then
        echo "Execution identity collision" >&2
        exit 3
    fi
fi

tmp=$(mktemp -d "$PWD/.release-$component.XXXXXX")
export DOCKER_CONFIG="$tmp/docker"
mkdir -m 700 "$DOCKER_CONFIG"
compose=(docker compose --project-name nexoruta-demo --env-file .env -f compose.vps.yaml)
changed=false
record() {
    printf '%s component=%s run=%s attempt=%s sha=%s tag=%s result=%s\n' \
        "$(date -u +%FT%TZ)" "$component" "$run_number" "$attempt" "$sha" "$tag" "$1" >> "$journal"
}
finish() {
    rc=$?
    trap - EXIT INT TERM HUP
    set +e
    preserve=false
    if [[ $changed == true && $rc != 0 ]]; then
        # Restore exact env bytes, but roll containers back to their actual immutable image IDs.
        if ! { cp -p "$tmp/env.before" "$tmp/env.restore" && mv -f "$tmp/env.restore" .env; }; then
            preserve=true
            record failed_env_restore
            echo "Environment restoration failed; manual recovery required" >&2
        elif (( ${#previous_services[@]} )); then
            if "${compose[@]}" -f "$tmp/rollback.yaml" up -d --no-deps --wait --wait-timeout 180 "${previous_services[@]}"; then
                if (( ${#previous_services[@]} == ${#services[@]} )); then
                    record failed_rolled_back
                else
                    record failed_partial_rollback
                fi
            else
                preserve=true
                record failed_rollback_failed
                echo "Release and rollback failed; manual recovery required" >&2
            fi
        else
            record failed_no_previous_images
            echo "Release failed; no previous containers exist to restore" >&2
        fi
        if (( ${#previous_services[@]} < ${#services[@]} )); then
            echo "Some services had no previous image; no successful full rollback is claimed" >&2
        fi
    elif (( rc != 0 )); then
        record failed_before_update
    fi
    # Never retain registry credentials with manual recovery artifacts.
    if ! rm -rf -- "$DOCKER_CONFIG"; then
        preserve=true
        echo "Registry credential cleanup failed; remove the temporary Docker config immediately" >&2
        rc=1
    fi
    if [[ $preserve == true ]]; then
        printf 'component=%s run=%s attempt=%s sha=%s tag=%s\n' \
            "$component" "$run_number" "$attempt" "$sha" "$tag" > "$tmp/recovery.txt"
        echo "Recovery files retained in $tmp (contains the original private .env)" >&2
    else
        rm -rf -- "$tmp"
    fi
    exit "$rc"
}
trap finish EXIT
trap 'exit 130' INT
trap 'exit 143' TERM HUP

# Record the watermark before pull: even a newer failed execution fences older runs.
printf '%s %s %s %s\n' "$run_number" "$attempt" "$sha" "$tag" > "$tmp/state"
mv -f "$tmp/state" "$state"
record started
printf '%s' "${NEXORUTA_PULL_TOKEN:?GHCR pull token required}" |
    docker login ghcr.io --username "$pull_user" --password-stdin >/dev/null
unset NEXORUTA_PULL_TOKEN
cp -p .env "$tmp/env.before"
previous_services=()
printf 'services:\n' > "$tmp/rollback.yaml"
for service in "${services[@]}"; do
    id=$("${compose[@]}" ps -a -q "$service")
    if [[ -n $id ]]; then
        [[ $id != *$'\n'* ]] || { echo "Multiple containers for $service" >&2; exit 2; }
        image=$(docker inspect --format '{{.Image}}' "$id")
        [[ $image =~ ^sha256:[0-9a-f]{64}$ ]] || { echo "Invalid previous image ID" >&2; exit 2; }
        previous_services+=("$service")
        printf '  %s:\n    image: "%s"\n' "$service" "$image" >> "$tmp/rollback.yaml"
        printf '%s previous_image=%s\n' "$service" "$image" >> "$journal"
    fi
done
cp -p .env "$tmp/env.next"
for key in "${keys[@]}"; do
    # Never source .env: passwords and shell metacharacters are data, not commands.
    awk -v key="$key" -v tag="$tag" '
        $0 ~ "^[[:space:]]*(export[[:space:]]+)?" key "[[:space:]]*=" {
            if (!found++) print key "=" tag
            next
        }
        { print }
        END { if (!found) print key "=" tag }
    ' "$tmp/env.next" > "$tmp/env.content"
    cat "$tmp/env.content" > "$tmp/env.next"
done
changed=true
mv -f "$tmp/env.next" .env
"${compose[@]}" pull "${services[@]}"
"${compose[@]}" up -d --no-deps --wait --wait-timeout 180 "${services[@]}"
for i in "${!services[@]}"; do
    id=$("${compose[@]}" ps -q "${services[$i]}")
    [[ -n $id && $id != *$'\n'* ]]
    status=$(docker inspect --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id")
    [[ $status == 'running healthy' ]] || { echo "Service not healthy: ${services[$i]}" >&2; exit 1; }
    actual=$(docker inspect --format '{{.Config.Image}}' "$id")
    [[ $actual == "${images[$i]}:$tag" ]] || { echo "Unexpected service image" >&2; exit 1; }
    printf '%s image=%s source=%s\n' "${services[$i]}" "$actual" "$sha" >> "$journal"
done
record healthy
changed=false
echo "Release healthy: $component $sha"

