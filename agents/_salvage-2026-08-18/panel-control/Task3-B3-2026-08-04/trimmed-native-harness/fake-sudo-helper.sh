#!/usr/bin/env bash
set -Eeuo pipefail

[[ "${1:-}" == "-n" ]] || { printf 'expected -n\n' >&2; exit 78; }
shift
[[ $# -ge 2 ]] || { printf 'missing helper/action\n' >&2; exit 78; }
helper_path="$1"
shift
action="$1"
shift
: "${SMM_FAKE_RULES_FILE:?}"
: "${SMM_FAKE_CALL_LOG:?}"
printf '%s\t%s\n' "$action" "$*" >>"$SMM_FAKE_CALL_LOG"

case "$action" in
  reconcile-status)
    [[ $# -eq 0 ]] || exit 78
    printf 'complete\n'
    ;;
  link-list)
    [[ $# -eq 0 ]] || exit 78
    cat -- "$SMM_FAKE_RULES_FILE"
    ;;
  link-disconnect)
    [[ $# -eq 4 ]] || exit 78
    expected="$1"$'\t'"$2"$'\t'"$3"$'\t'"$4"
    current="$(cat -- "$SMM_FAKE_RULES_FILE")"
    [[ "$current" == "$expected" ]] || {
      printf 'unexpected disconnect: %s (helper=%s)\n' "$expected" "$helper_path" >&2
      exit 78
    }
    : >"$SMM_FAKE_RULES_FILE"
    ;;
  *)
    printf 'unexpected action: %s\n' "$action" >&2
    exit 78
    ;;
esac
