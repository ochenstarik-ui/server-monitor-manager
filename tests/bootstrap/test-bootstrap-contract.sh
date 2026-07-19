#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
helper="$root/deploy/ochenstarik-smm-policy-apply"

help_output="$(bash "$bootstrap" --help)"
version_output="$(bash "$bootstrap" --version)"

grep -Fq "install-control ARCHIVE PUBLIC_HOST" <<<"$help_output"
grep -Fq "install-agent ARCHIVE NODE_ID CONTROL_URL CA_CERT" <<<"$help_output"
grep -Fq "SMM_ENROLL_TOKEN" <<<"$help_output"
grep -Fq "node-token NODE_ID" <<<"$help_output"
grep -Eq '^ochenstarik-server-monitor-manager [0-9]+\.[0-9]+\.[0-9]+-' <<<"$version_output"

if bash "$bootstrap" unsupported-action >/dev/null 2>&1; then
    printf '%s\n' "unsupported bootstrap action unexpectedly succeeded" >&2
    exit 1
fi

if env -u SUDO_UID -u SUDO_USER bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>&1; then
    printf '%s\n' "policy helper unexpectedly applied an unconfigured rule" >&2
    exit 1
fi

printf '%s\n' "BOOTSTRAP_CONTRACT=PASS"
