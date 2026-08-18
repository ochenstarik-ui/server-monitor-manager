#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
setup="$root/deploy/smm-setup.sh"
linux_docs="$root/docs/linux-bootstrap.md"
windows_workflow="$root/.github/workflows/windows-build.yml"
release_workflow="$root/.github/workflows/linux-release.yml"

default_tag="$(sed -n 's/^readonly DEFAULT_RELEASE_TAG="\([^"]*\)"$/\1/p' "$setup")"
[[ "$default_tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || {
    printf 'invalid or missing DEFAULT_RELEASE_TAG: %s\n' "$default_tag" >&2
    exit 1
}

grep -Fq "SMM_TAG         Release tag (default: $default_tag)" "$setup" || {
    printf '%s\n' 'smm-setup help default does not match DEFAULT_RELEASE_TAG' >&2
    exit 1
}

windows_tag="$(sed -n 's/^[[:space:]]*SMM_TEST_RELEASE_TAG:[[:space:]]*\([^[:space:]]*\)$/\1/p' "$windows_workflow")"
[[ "$windows_tag" == "$default_tag" ]] || {
    printf 'Windows live-release tag %s does not match %s\n' "$windows_tag" "$default_tag" >&2
    exit 1
}

mapfile -t documented_tags < <(
    grep -Eo 'v0\.1\.0-alpha\.[0-9]+' "$linux_docs" | sort -u
)
[[ ${#documented_tags[@]} -gt 0 ]] || {
    printf '%s\n' 'Linux bootstrap documentation has no release download tag' >&2
    exit 1
}
for documented_tag in "${documented_tags[@]}"; do
    [[ "$documented_tag" == "$default_tag" ]] || {
        printf 'Documented release tag %s does not match %s\n' "$documented_tag" "$default_tag" >&2
        exit 1
    }
done

grep -Fq 'REHEARSAL_TAG: ${{ inputs.tag }}' "$release_workflow"
[[ "$(grep -Fc 'REHEARSAL_TAG: ${{ inputs.tag }}' "$release_workflow")" -eq 4 ]]
grep -Fq 'grep -Fq "readonly DEFAULT_RELEASE_TAG=\"${RELEASE_TAG}\""' "$release_workflow"

printf 'RELEASE_VERSION_CONSISTENCY=PASS tag=%s\n' "$default_tag"
