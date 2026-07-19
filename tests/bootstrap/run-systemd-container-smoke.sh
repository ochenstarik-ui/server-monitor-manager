#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
base_image="${1:?usage: run-systemd-container-smoke.sh BASE_IMAGE ARCHIVE BOOTSTRAP}"
archive="$(realpath "${2:?archive is required}")"
bootstrap="$(realpath "${3:?bootstrap is required}")"
name="smm-systemd-${RANDOM}-${RANDOM}"
image="smm-systemd-smoke:${base_image//[:\/]/-}"
port="17443"
smoke_dir="/root/smm-smoke"
remote_archive="$smoke_dir/release.tar.gz"
remote_bootstrap="$smoke_dir/ochenstarik-server-monitor-manager.sh"

cleanup() {
    docker rm -f "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker build \
    --build-arg "BASE_IMAGE=$base_image" \
    -f "$root/tests/bootstrap/systemd-container.Dockerfile" \
    -t "$image" \
    "$root"
docker run --detach --privileged --cgroupns=private \
    --tmpfs /run --tmpfs /run/lock --name "$name" "$image" >/dev/null

for _ in {1..30}; do
    if docker exec "$name" systemctl is-system-running >/dev/null 2>&1; then
        break
    fi
    state="$(docker exec "$name" systemctl is-system-running 2>/dev/null || true)"
    [[ "$state" == "degraded" ]] && break
    sleep 1
done
state="$(docker exec "$name" systemctl is-system-running 2>/dev/null || true)"
[[ "$state" == "running" || "$state" == "degraded" ]] || {
    docker exec "$name" systemctl --failed --no-pager || true
    printf '%s\n' "container systemd did not finish booting: $state" >&2
    exit 1
}

docker exec "$name" install -d -m 0700 "$smoke_dir"
docker cp "$archive" "$name:$remote_archive"
docker cp "${archive}.sha256" "$name:${remote_archive}.sha256"
docker cp "$bootstrap" "$name:$remote_bootstrap"
docker exec "$name" chmod 0700 "$remote_bootstrap"
docker exec "$name" "$remote_bootstrap" preflight
docker exec "$name" "$remote_bootstrap" install-control \
    "$remote_archive" 127.0.0.1 "$port"
docker exec "$name" curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"
docker exec "$name" "$remote_bootstrap" install-control \
    "$remote_archive" 127.0.0.1 "$port"

docker restart "$name" >/dev/null
for _ in {1..60}; do
    if docker exec "$name" systemctl is-active --quiet ochenstarik-smm-control.service; then
        break
    fi
    sleep 1
done
docker exec "$name" systemctl is-active --quiet ochenstarik-smm-control.service
docker exec "$name" curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"
docker exec "$name" /usr/local/sbin/ochenstarik-smm-emergency status

printf '%s\n' "SYSTEMD_CONTAINER_SMOKE=PASS image=$base_image"
