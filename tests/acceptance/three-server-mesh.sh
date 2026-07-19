#!/usr/bin/env bash
set -Eeuo pipefail

required=(curl jq openssl ssh nc base64)
for command_name in "${required[@]}"; do
  command -v "$command_name" >/dev/null || {
    echo "Missing required command: $command_name" >&2
    exit 2
  }
done

: "${HUB_SSH_HOST:?Set HUB_SSH_HOST}"
: "${HUB_SSH_USER:?Set HUB_SSH_USER}"
: "${SOURCE_SSH_HOST:?Set SOURCE_SSH_HOST}"
: "${SOURCE_SSH_USER:?Set SOURCE_SSH_USER}"
: "${HOME_WG_IP:?Set HOME_WG_IP}"
: "${SECOND_WG_IP:?Set SECOND_WG_IP}"

HUB_SSH_PORT="${HUB_SSH_PORT:-22}"
SOURCE_SSH_PORT="${SOURCE_SSH_PORT:-22}"
SOURCE_NODE_ID="${SOURCE_NODE_ID:-ai-agent}"
HOME_NODE_ID="${HOME_NODE_ID:-home}"
SECOND_NODE_ID="${SECOND_NODE_ID:-second}"
TARGET_PORT="${TARGET_PORT:-22}"
CONTROL_DEVICE_ID="acceptance-$(date +%s)"
INSTALLER_COMMAND="${INSTALLER_COMMAND:-sudo /usr/local/sbin/ochenstarik-server-monitor-manager.sh}"
WORK_DIRECTORY="$(mktemp -d)"
LINK_HOME_ID=''
LINK_SECOND_ID=''

cleanup() {
  rm -rf "$WORK_DIRECTORY"
}
trap cleanup EXIT

ssh_options=(-o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new)
if [[ -n "${SSH_IDENTITY_FILE:-}" ]]; then
  ssh_options+=(-i "$SSH_IDENTITY_FILE")
fi

hub_ssh() {
  ssh "${ssh_options[@]}" -p "$HUB_SSH_PORT" \
    "$HUB_SSH_USER@$HUB_SSH_HOST" "$@"
}

source_ssh() {
  ssh "${ssh_options[@]}" -p "$SOURCE_SSH_PORT" \
    "$SOURCE_SSH_USER@$SOURCE_SSH_HOST" "$@"
}

expect_reachable() {
  local ip="$1"
  source_ssh "nc -z -w 5 '$ip' '$TARGET_PORT'"
}

expect_blocked() {
  local ip="$1"
  if source_ssh "nc -z -w 5 '$ip' '$TARGET_PORT'"; then
    echo "Unexpected access from $SOURCE_NODE_ID to $ip:$TARGET_PORT" >&2
    exit 1
  fi
}

decode_base64url() {
  local value="${1//-/+}"
  value="${value//_/\/}"
  case $((${#value} % 4)) in
    2) value+='==' ;;
    3) value+='=' ;;
    1) echo 'Invalid base64url value' >&2; return 1 ;;
  esac
  printf '%s' "$value" | base64 --decode
}

echo '[1/11] Checking installed services on all three nodes'
hub_ssh "sudo systemctl is-active ochenstarik-smm-control.service >/dev/null"
source_ssh "sudo systemctl is-active ochenstarik-smm-agent.service >/dev/null"

echo '[2/11] Creating an isolated operator identity for this acceptance run'
device_code="$(hub_ssh "$INSTALLER_COMMAND control-device-code '$CONTROL_DEVICE_ID'" | tr -d '\r' | grep -o 'SMMDEV1-[A-Za-z0-9_-]*' | tail -n1)"
[[ -n "$device_code" ]] || { echo 'Hub did not return SMMDEV1 code' >&2; exit 1; }
decode_base64url "${device_code#SMMDEV1-}" >"$WORK_DIRECTORY/device.env"
control_url="$(sed -n 's/^URL=//p' "$WORK_DIRECTORY/device.env")"
token="$(sed -n 's/^TOKEN=//p' "$WORK_DIRECTORY/device.env")"
ca_base64="$(sed -n 's/^CA=//p' "$WORK_DIRECTORY/device.env")"
[[ "$control_url" == https://* && -n "$token" && -n "$ca_base64" ]] || {
  echo 'Invalid SMMDEV1 payload' >&2
  exit 1
}
printf '%s' "$ca_base64" | base64 --decode >"$WORK_DIRECTORY/ca.der"
openssl x509 -inform DER -in "$WORK_DIRECTORY/ca.der" -out "$WORK_DIRECTORY/ca.pem"
openssl ecparam -name prime256v1 -genkey -noout -out "$WORK_DIRECTORY/operator.key"
openssl req -new -sha256 -key "$WORK_DIRECTORY/operator.key" \
  -subj "/CN=$CONTROL_DEVICE_ID" -out "$WORK_DIRECTORY/operator.csr"
jq -n \
  --arg deviceId "$CONTROL_DEVICE_ID" \
  --arg token "$token" \
  --arg csr "$(cat "$WORK_DIRECTORY/operator.csr")" \
  --arg idempotencyKey "$(cat /proc/sys/kernel/random/uuid)" \
  '{deviceId:$deviceId,token:$token,certificateSigningRequestPem:$csr,idempotencyKey:$idempotencyKey}' \
  >"$WORK_DIRECTORY/enroll.json"
curl --fail --silent --show-error --cacert "$WORK_DIRECTORY/ca.pem" \
  -H 'Content-Type: application/json' --data-binary @"$WORK_DIRECTORY/enroll.json" \
  "$control_url/api/v1/device-enroll" >"$WORK_DIRECTORY/enrollment.json"
jq -r '.certificatePem' "$WORK_DIRECTORY/enrollment.json" >"$WORK_DIRECTORY/operator.pem"

api_get() {
  curl --fail --silent --show-error --cacert "$WORK_DIRECTORY/ca.pem" \
    --cert "$WORK_DIRECTORY/operator.pem" --key "$WORK_DIRECTORY/operator.key" \
    "$control_url$1"
}

api_post() {
  local path="$1"
  local body="$2"
  curl --fail --silent --show-error --cacert "$WORK_DIRECTORY/ca.pem" \
    --cert "$WORK_DIRECTORY/operator.pem" --key "$WORK_DIRECTORY/operator.key" \
    -H 'Content-Type: application/json' --data-binary "$body" "$control_url$path"
}

create_link() {
  local target="$1"
  local ttl="$2"
  api_post '/api/v1/control/links' "$(jq -cn \
    --arg source "$SOURCE_NODE_ID" --arg target "$target" \
    --argjson port "$TARGET_PORT" --argjson ttl "$ttl" \
    --arg key "$(cat /proc/sys/kernel/random/uuid)" \
    '{sourceNodeId:$source,targetNodeId:$target,protocol:"tcp",port:$port,ttlMinutes:$ttl,reason:"three-server acceptance",idempotencyKey:$key}')"
}

disable_link() {
  local id="$1"
  api_post "/api/v1/control/links/$id/disable" \
    "$(jq -cn --arg key "$(cat /proc/sys/kernel/random/uuid)" '{idempotencyKey:$key}')"
}

echo '[3/11] Confirming all expected Agent identities are online'
agents="$(api_get '/api/v1/control/agents')"
for node in "$SOURCE_NODE_ID" "$HOME_NODE_ID" "$SECOND_NODE_ID"; do
  jq -e --arg node "$node" '.[] | select(.nodeId == $node)' <<<"$agents" >/dev/null || {
    echo "Control Hub does not contain Agent $node" >&2
    exit 1
  }
done

echo '[4/11] Creating independent Links to home and second server'
home_link="$(create_link "$HOME_NODE_ID" 0)"
second_link="$(create_link "$SECOND_NODE_ID" 0)"
LINK_HOME_ID="$(jq -r '.id' <<<"$home_link")"
LINK_SECOND_ID="$(jq -r '.id' <<<"$second_link")"
jq -e '.actualState == "Active"' <<<"$home_link" >/dev/null
jq -e '.actualState == "Active"' <<<"$second_link" >/dev/null

echo '[5/11] Verifying routed access through both Links'
expect_reachable "$HOME_WG_IP"
expect_reachable "$SECOND_WG_IP"

echo '[6/11] Disabling only the second Link'
disable_link "$LINK_SECOND_ID" | jq -e '.actualState == "Disabled"' >/dev/null
expect_reachable "$HOME_WG_IP"
expect_blocked "$SECOND_WG_IP"

echo '[7/11] Verifying automatic TTL expiration'
ttl_link="$(create_link "$SECOND_NODE_ID" 1)"
ttl_id="$(jq -r '.id' <<<"$ttl_link")"
expect_reachable "$SECOND_WG_IP"
deadline=$((SECONDS + 120))
while ((SECONDS < deadline)); do
  state="$(api_get '/api/v1/control/links' | jq -r --arg id "$ttl_id" '.[] | select(.id == $id) | .actualState')"
  [[ "$state" == 'Disabled' ]] && break
  sleep 5
done
[[ "${state:-}" == 'Disabled' ]] || { echo 'TTL Link did not become Disabled' >&2; exit 1; }
expect_blocked "$SECOND_WG_IP"
expect_reachable "$HOME_WG_IP"

echo '[8/11] Creating and validating a Control backup'
backup_path="$(hub_ssh "sudo systemd-run --wait --pipe --quiet --collect --uid=ochenstarik-smm-control --gid=ochenstarik-smm-control -p EnvironmentFile=/etc/ochenstarik-server-monitor-manager/control.env /usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control backup-create" | grep '/backup-' | tail -n1)"
[[ "$backup_path" == */backup-* ]] || { echo 'Backup command did not return a backup path' >&2; exit 1; }
hub_ssh "sudo test -s '$backup_path/manifest.json' && sudo test -s '$backup_path/control.db' && sudo test -s '$backup_path/control-ca.pfx'"

if [[ "${SMM_ACCEPT_RESTORE:-0}" == '1' ]]; then
  echo '[9/11] Restoring the verified backup and restarting Control'
  hub_ssh "sudo sh -c 'set -a; . /etc/ochenstarik-server-monitor-manager/control.env; set +a; systemctl stop ochenstarik-smm-control.service; /usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control backup-restore \"\$1\"; status=\$?; systemctl start ochenstarik-smm-control.service; exit \$status' sh '$backup_path'"
  for _ in {1..30}; do
    if api_get '/healthz' >/dev/null 2>&1; then
      break
    fi
    sleep 2
  done
  api_get '/healthz' >/dev/null
  expect_reachable "$HOME_WG_IP"
  expect_blocked "$SECOND_WG_IP"
else
  echo '[9/11] Restore check skipped; set SMM_ACCEPT_RESTORE=1 to enable it'
fi

if [[ "${SMM_ACCEPT_REBOOT:-0}" == '1' ]]; then
  echo '[10/11] Rebooting the Hub and source Node and rechecking policy state'
  hub_ssh 'sudo systemctl reboot' || true
  source_ssh 'sudo systemctl reboot' || true
  sleep 15
  for _ in {1..30}; do
    if hub_ssh 'true' >/dev/null 2>&1 && source_ssh 'true' >/dev/null 2>&1; then
      break
    fi
    sleep 5
  done
  expect_reachable "$HOME_WG_IP"
  expect_blocked "$SECOND_WG_IP"
else
  echo '[10/11] Reboot check skipped; set SMM_ACCEPT_REBOOT=1 to enable it'
fi

echo '[11/11] Revoking the temporary Operator certificate'
api_post "/api/v1/control/devices/$CONTROL_DEVICE_ID/reenroll" \
  "$(jq -cn --arg key "$(cat /proc/sys/kernel/random/uuid)" \
    '{reason:"three-server acceptance completed",idempotencyKey:$key}')" \
  | jq -e '.entityType == "Operator"' >/dev/null
if api_get '/api/v1/control/agents' >/dev/null 2>&1; then
  echo 'Revoked acceptance Operator certificate is still authorized' >&2
  exit 1
fi

echo 'THREE_SERVER_ACCEPTANCE=PASS'
