# WSL Linux toolchain evidence

Дата: 2026-08-04

## Установка

- Distribution: Ubuntu under WSL2.
- User-local install directory: `/home/starik/.dotnet`.
- Installer downloaded and installed SDK `10.0.302` successfully.
- Downloaded SDK archive size reported by installer: `235808828` bytes.
- No sudo/system package changes were made.

## First-run dependency finding

Initial `dotnet --info` exited `134` because the minimal Ubuntu image has no ICU package. This did not indicate an incomplete SDK install.

Because the repository projects set `InvariantGlobalization=true`, the CLI was retried with:

```text
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
DOTNET_ROOT=/home/starik/.dotnet
PATH=/home/starik/.dotnet:$PATH
```

## Verified result

```text
.NET SDK: 10.0.302
MSBuild: 18.6.11+35b593beb
OS: ubuntu 26.04
RID: linux-x64
Host runtime: 10.0.10
Microsoft.AspNetCore.App: 10.0.10
Microsoft.NETCore.App: 10.0.10
DOTNET_WSL_READY=PASS
```

This evidence confirms the Linux test prerequisite only. Actual B-3R Linux tests and trimmed orphan verification must be recorded separately after the repair diff is finalized.
