# Contributing to Server Monitor Manager

Thank you for your interest in contributing. This document describes how to
build the project, run tests, and submit changes.

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- A Linux host or WSL for server-side components (see [Testing](#testing))
- `shellcheck` for shell script linting

### Build

```bash
dotnet build ServerMonitorManager.slnx --configuration Release
```

### Publish (Linux binaries)

```bash
dotnet publish src/ServerMonitorManager.Agent/ServerMonitorManager.Agent.csproj \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o out/agent

dotnet publish src/ServerMonitorManager.Control/ServerMonitorManager.Control.csproj \
  --configuration Release --runtime linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -o out/control
```

## Testing

### Unit and integration tests

```bash
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj \
  --configuration Release
```

> **Important:** The Control test suite must be run on **Linux**. A subset of
> tests is gated with `[SupportedOSPlatform("linux")]` / `OperatingSystem.IsLinux()`
> and will be **silently skipped on Windows**. CI always runs these on Ubuntu;
> do not interpret a green local run on Windows as full test coverage.

### Bootstrap contract tests

```bash
bash tests/bootstrap/test-bootstrap-contract.sh
bash tests/bootstrap/test-enrollment-token-argv.sh
```

### Shell script linting

```bash
shellcheck --severity=error deploy/ochenstarik-server-monitor-manager.sh
shellcheck --severity=error deploy/ochenstarik-smm-policy-apply
shellcheck --severity=error deploy/ochenstarik-smm-emergency
```

### Windows Desktop tests

```powershell
./tests/windows/Test-DesktopContracts.ps1
dotnet test tests/ServerMonitorManager.Desktop.Security.Tests/ServerMonitorManager.Desktop.Security.Tests.csproj --configuration Release
```

## Code Style

Verify formatting before committing:

```bash
dotnet format ServerMonitorManager.slnx --verify-no-changes
```

## Submitting Changes

**One PR — one topic.** Do not bundle unrelated changes in a single pull
request. Small, focused PRs are reviewed faster and are easier to revert if
needed.

1. Fork the repository and create a branch from `main`.
2. Make your changes, keeping the scope focused.
3. Run all relevant tests locally (see above).
4. Open a pull request using the PR template — fill in **all sections**,
   including what was *not* tested and why.

## What Not to Change

See [`TASK.md`](TASK.md) and inline comments in the codebase for files that
are currently locked by parallel work streams. When in doubt, ask in the issue
or PR before making changes to files in `src/`, `deploy/`, or `tests/`.
