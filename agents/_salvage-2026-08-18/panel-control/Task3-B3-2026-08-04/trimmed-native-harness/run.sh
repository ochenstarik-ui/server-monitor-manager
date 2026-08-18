#!/usr/bin/env bash
set -Eeuo pipefail

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

source_root='/mnt/c/Users/Ochenstarik/projects/server-monitor-manager-task3-b3'
evidence_root='/mnt/c/Users/Ochenstarik/Сюда/Panel control/Task3-B3-2026-08-04'
runtime="$(mktemp -d "$HOME/hermes-b3r-native.XXXXXXXX")"
snapshot="$runtime/source"
publish="$runtime/publish"
mkdir -p "$snapshot" "$publish" "$runtime/backups"

tar --exclude=.git --exclude=bin --exclude=obj -C "$source_root" -cf - . | tar -C "$snapshot" -xf -
cp "$evidence_root/trimmed-native-harness/fake-sudo-helper.sh" "$runtime/fake-sudo-helper.sh"
chmod 700 "$runtime/fake-sudo-helper.sh"
printf 'source\ttarget\ttcp\t2222\n' >"$runtime/rules.tsv"
: >"$runtime/helper-calls.tsv"

cd "$snapshot"
dotnet restore tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj \
  -c Release --no-restore --verbosity minimal

dotnet publish src/ServerMonitorManager.Control/ServerMonitorManager.Control.csproj \
  -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=true -o "$publish"

test -x "$publish/ochenstarik-smm-control"
publish_hash="$(sha256sum "$publish/ochenstarik-smm-control.dll" | cut -d' ' -f1)"
printf 'PUBLISHED_CONTROL_SHA256=%s\n' "$publish_hash"

export SMM_FAKE_RULES_FILE="$runtime/rules.tsv"
export SMM_FAKE_CALL_LOG="$runtime/helper-calls.tsv"
export Control__DatabasePath="$runtime/control.db"
export Control__BackupDirectory="$runtime/backups"
export Control__CertificateAuthorityPath="$runtime/unused-control-ca.pfx"
export Control__PrivilegeEscalationPath="$runtime/fake-sudo-helper.sh"
export Control__HubHelperPath="$runtime/unused-policy-helper"
export Control__LinkReconciliationSeconds=30
export ASPNETCORE_URLS='http://127.0.0.1:0'

printf 'CONFIG_DATABASE_PATH=%s\n' "$Control__DatabasePath"
"$publish/ochenstarik-smm-control" \
  --Control:DatabasePath "$runtime/control.db" \
  --Control:BackupDirectory "$runtime/backups" \
  --Control:CertificateAuthorityPath "$runtime/unused-control-ca.pfx" \
  --Control:PrivilegeEscalationPath "$runtime/fake-sudo-helper.sh" \
  --Control:HubHelperPath "$runtime/unused-policy-helper" \
  --Control:LinkReconciliationSeconds 30 \
  --urls 'http://127.0.0.1:0' \
  >"$runtime/control.stdout.log" 2>"$runtime/control.stderr.log" &
control_pid=$!

cleanup_process() {
  if kill -0 "$control_pid" 2>/dev/null; then
    kill "$control_pid" 2>/dev/null || true
    wait "$control_pid" 2>/dev/null || true
  fi
}
trap cleanup_process EXIT

verified=0
for _ in $(seq 1 120); do
  if ! kill -0 "$control_pid" 2>/dev/null; then
    printf 'CONTROL_EXITED_EARLY\n' >&2
    cat "$runtime/control.stdout.log" >&2 || true
    cat "$runtime/control.stderr.log" >&2 || true
    exit 1
  fi
  if [[ -f "$runtime/control.db" ]] && python3 - "$runtime/control.db" <<'PY'
import json, sqlite3, sys
path = sys.argv[1]
try:
    connection = sqlite3.connect(path, timeout=1)
    row = connection.execute(
        "SELECT actor, action, subject, details_json FROM audit "
        "WHERE action='link.orphan-removed' ORDER BY sequence DESC LIMIT 1"
    ).fetchone()
finally:
    try:
        connection.close()
    except Exception:
        pass
if row is None:
    raise SystemExit(1)
actor, action, subject, details_json = row
details = json.loads(details_json)
expected = {
    "sourceNodeId": "source",
    "targetNodeId": "target",
    "protocol": "tcp",
    "port": 2222,
}
if actor != "system:reconcile" or action != "link.orphan-removed":
    raise SystemExit(2)
if subject != "source:target:tcp:2222" or details != expected:
    raise SystemExit(3)
print(f"AUDIT={actor}|{action}|{subject}|{details_json}")
PY
  then
    verified=1
    break
  fi
  sleep 0.5
done

[[ "$verified" == 1 ]] || {
  printf 'ORPHAN_AUDIT_TIMEOUT\n' >&2
  cat "$runtime/control.stdout.log" >&2 || true
  cat "$runtime/control.stderr.log" >&2 || true
  exit 1
}

[[ ! -s "$runtime/rules.tsv" ]] || { printf 'ORPHAN_RULE_REMAINS\n' >&2; exit 1; }
list_calls="$(awk -F '\t' '$1 == "link-list" { count++ } END { print count + 0 }' "$runtime/helper-calls.tsv")"
disconnect_calls="$(awk -F '\t' '$1 == "link-disconnect" { count++ } END { print count + 0 }' "$runtime/helper-calls.tsv")"
[[ "$list_calls" == 2 ]] || { printf 'LIST_CALLS=%s\n' "$list_calls" >&2; exit 1; }
[[ "$disconnect_calls" == 1 ]] || { printf 'DISCONNECT_CALLS=%s\n' "$disconnect_calls" >&2; exit 1; }

printf 'TRIMMED_NATIVE_ORPHAN_AUDIT=PASS\n'
printf 'HELPER_CALLS=list:%s,disconnect:%s\n' "$list_calls" "$disconnect_calls"
printf 'RUNTIME_DIR=%s\n' "$runtime"
cat "$runtime/helper-calls.tsv"
cleanup_process
trap - EXIT
