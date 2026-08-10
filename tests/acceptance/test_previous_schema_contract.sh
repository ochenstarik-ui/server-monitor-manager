#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
test_source="$root/tests/ServerMonitorManager.Control.Tests/SchemaCompatibilityTests.cs"
workflow="$root/.github/workflows/linux-control-agent.yml"
arm64_test="$root/tests/acceptance/test_previous_schema_arm64.sh"
fixture="$root/tests/fixtures/control-v0.1.0-alpha.7.db"

require_contains() {
    local text="$1" file="$2"
    grep -Fq "$text" "$file" || {
        printf 'required compatibility evidence is missing from %s: %s\n' "$file" "$text" >&2
        exit 1
    }
}

[[ -s "$fixture" ]] || {
    printf '%s\n' 'published previous-schema database fixture is missing' >&2
    exit 1
}
require_contains 'PublishedAlpha7DatabaseRemainsReadableAndRestorable' "$test_source"
require_contains 'Assert.Equal(schemaVersionBefore, schemaVersionAfter);' "$test_source"
require_contains 'ListAgentsAsync' "$test_source"
require_contains 'ResolveIdentityAsync' "$test_source"
require_contains 'ListLinksAsync' "$test_source"
require_contains 'GetProvisioningJobAsync' "$test_source"
require_contains 'FROM audit' "$test_source"
require_contains 'CreateAsync' "$test_source"
require_contains 'RestoreAsync' "$test_source"

if grep -Fq 'if (!File.Exists' "$test_source" || grep -Fq 'Generate reference alpha.7' "$workflow"; then
    printf '%s\n' 'previous-schema coverage is still skip- or generation-prone' >&2
    exit 1
fi

require_contains 'Publish control arm64 compatibility binary' "$workflow"
require_contains 'Verify previous schema with arm64 published Control' "$workflow"
require_contains 'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1' "$arm64_test"

printf '%s\n' 'PREVIOUS_SCHEMA_CONTRACT=PASS'
