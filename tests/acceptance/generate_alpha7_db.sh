#!/bin/bash
set -euo pipefail

OUTPUT_DB=$(realpath "${1:-alpha7.db}")

if [ -f "$OUTPUT_DB" ]; then
    echo "Database $OUTPUT_DB already exists."
    exit 0
fi

echo "Generating reference database from v0.1.0-alpha.7 to $OUTPUT_DB"
WORK_DIR=$(mktemp -d)
trap 'rm -rf "$WORK_DIR"' EXIT

git clone --depth 1 -b v0.1.0-alpha.7 https://github.com/ochenstarik-ui/server-monitor-manager.git "$WORK_DIR/repo"

cat << 'EOF' > "$WORK_DIR/repo/tests/ServerMonitorManager.Control.Tests/GenerateDbTest.cs"
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ServerMonitorManager.Control;
using Microsoft.Extensions.Options;

namespace ServerMonitorManager.Control.Tests;

public class GenerateDbTest
{
    [Fact]
    public async Task GenerateReferenceDatabase()
    {
        var dbPath = System.Environment.GetEnvironmentVariable("OUTPUT_DB_PATH");
        var store = new ControlStore(Options.Create(new ControlOptions { DatabasePath = dbPath }));
        await store.InitializeAsync(CancellationToken.None);
    }
}
EOF

export OUTPUT_DB_PATH="$OUTPUT_DB"
cd "$WORK_DIR/repo"
dotnet test tests/ServerMonitorManager.Control.Tests --filter "GenerateReferenceDatabase"
