#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

[[ "$(uname -s)" == "Linux" ]] || {
    printf '%s\n' "enrollment argv test requires Linux" >&2
    exit 1
}

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
agent="$root/src/ServerMonitorManager.Agent/bin/Release/net10.0/ochenstarik-smm-agent.dll"
[[ -f "$agent" ]] || {
    printf '%s\n' "build the Release agent before running this test" >&2
    exit 1
}

fixture="$(mktemp -d -t smm-enrollment-argv.XXXXXXXX)"
pid=""
cleanup() {
    if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    fi
    rm -rf -- "$fixture"
}
trap cleanup EXIT

token="smm-secret-$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"
state_dir="$fixture/state"
enrollment_dir="$fixture/enrollment"
token_file="$enrollment_dir/enroll-token"
ca_file="$fixture/control-ca.crt"
mkdir -p "$state_dir" "$enrollment_dir"
printf '%s' "$token" >"$token_file"
chmod 0600 "$token_file"
openssl req -x509 -newkey rsa:2048 -nodes -subj /CN=smm-test-ca \
    -keyout "$fixture/control-ca.key" -out "$ca_file" -days 1 >/dev/null 2>&1

SMM_NodeId=argv-test \
SMM_ControlUrl=https://192.0.2.1:7443 \
SMM_StateDirectory="$state_dir" \
SMM_EnrollmentTokenDirectory="$enrollment_dir" \
SMM_CertificateAuthorityPath="$ca_file" \
SMM_EnrollTokenFile="$token_file" \
    dotnet "$agent" >"$fixture/agent.log" 2>&1 &
pid="$!"

for _ in {1..500}; do
    if [[ ! -e "$token_file" ]]; then
        break
    fi
    if ! kill -0 "$pid" 2>/dev/null; then
        cat "$fixture/agent.log" >&2
        printf '%s\n' "agent exited before consuming its enrollment token" >&2
        exit 1
    fi
    sleep 0.01
done
[[ ! -e "$token_file" ]] || {
    cat "$fixture/agent.log" >&2
    printf '%s\n' "agent did not delete its enrollment token file" >&2
    exit 1
}

cmdline="$(tr '\0' ' ' <"/proc/$pid/cmdline")"
[[ "$cmdline" != *"$token"* ]] || {
    printf '%s\n' "enrollment token is visible in /proc/$pid/cmdline" >&2
    exit 1
}

for process_cmdline in /proc/[0-9]*/cmdline; do
    [[ -r "$process_cmdline" ]] || continue
    cmdline="$(tr '\0' ' ' <"$process_cmdline" 2>/dev/null || true)"
    [[ "$cmdline" != *"$token"* ]] || {
        printf '%s\n' "enrollment token is visible in $process_cmdline" >&2
        exit 1
    }
done

printf '%s\n' "ENROLLMENT_TOKEN_ARGV=PASS"
