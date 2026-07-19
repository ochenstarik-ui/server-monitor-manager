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

docker cp "$archive" "$name:/tmp/release.tar.gz"
docker cp "${archive}.sha256" "$name:/tmp/release.tar.gz.sha256"
docker cp "$bootstrap" "$name:/tmp/ochenstarik-server-monitor-manager.sh"
docker exec "$name" chmod 0700 /tmp/ochenstarik-server-monitor-manager.sh
docker exec "$name" /tmp/ochenstarik-server-monitor-manager.sh preflight
docker exec "$name" /tmp/ochenstarik-server-monitor-manager.sh install-control \
    /tmp/release.tar.gz 127.0.0.1 "$port"
docker exec "$name" curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"
docker exec "$name" /tmp/ochenstarik-server-monitor-manager.sh install-control \
    /tmp/release.tar.gz 127.0.0.1 "$port"

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
