# B-3R Linux RED evidence

Дата: 2026-08-04

Среда:

- WSL2 Ubuntu 26.04
- .NET SDK 10.0.302
- Runtime 10.0.10
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
- Worktree: `/mnt/c/Users/Ochenstarik/projects/server-monitor-manager-task3-b3`

Команды:

```bash
dotnet restore tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj -c Release --no-restore --verbosity minimal
```

Результат:

```text
Failed: 1
Passed: 88
Skipped: 0
Total: 89
Duration: 34 s
```

Ожидаемый RED regression:

```text
ServerMonitorManager.Control.Tests.LinkPolicyApplierIntegrationTests.LinuxHelperFailureKeepsKillSwitchPendingAcrossControlRestart
Assert.Equal() Failure
Expected: Tuple (1, 1, 0)
Actual:   Tuple (2, 2, 0)
LinkPolicyApplierIntegrationTests.cs:142
```

Вывод: Linux-only process-boundary integration protocol не соответствовал фактическому B-3/B-3R full-pass поведению. Это подтверждает H2/R5 и является RED evidence перед B-3R repair, а не финальным test result.

Restore также вывел стороннее предупреждение `NETSDK1188` о locale `zh-hant` в `Microsoft.TestPlatform.TestHost 18.0.1`; оно не являлось причиной test failure.
