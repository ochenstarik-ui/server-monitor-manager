# Test Evidence - Client Certificate Lifecycle Management

## 1. Automated Test Execution

Command executed:
```powershell
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj --configuration Release
```

Output summary:
```text
Тестовый запуск для C:\Users\Ochenstarik\projects\smm-antigravity\tests\ServerMonitorManager.Control.Tests\bin\Release\net10.0\ServerMonitorManager.Control.Tests.dll (.NETCoreApp,Version=v10.0)
Пройден!   : не пройдено     0, пройдено   101, пропущено     0, всего   101, длительность 11 s. - ServerMonitorManager.Control.Tests.dll (net10.0)
```

Includes test cases in `CertificateLifecycleTests.cs`:
1. `CertWithLessThanOneThirdRemainingIsRenewed_AndOldCertReplaced` - PASS
2. `CertWithSufficientRemainingLifetime_IsNotRenewed` - PASS
3. `HubUnavailable_AgentContinuesUsingExistingCert_MetricsPreserved` - PASS
4. `RevokedCert_CannotBeRenewed` - PASS
5. `RenewalRequest_WithDifferentNodeId_IsRejected` - PASS
6. `InterruptedPfxReplacement_ActiveCertRemainsIntact` - PASS
7. `OutOfRangeClientCertificateDays_ValidationFailsOnStart` - PASS

---

## 2. Trimmed Self-Contained Binary Publish & Startup Validation

Publish Command:
```powershell
dotnet publish src/ServerMonitorManager.Control/ServerMonitorManager.Control.csproj -c Release -r win-x64 --self-contained -p:PublishTrimmed=true
```

Validation Test Command with out-of-range option (`ClientCertificateDays = 999`):
```powershell
.\src\ServerMonitorManager.Control\bin\Release\net10.0\win-x64\publish\ochenstarik-smm-control.exe --Control:ClientCertificateDays 999
```

Output:
```text
Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: Invalid Control paths, heartbeat, retention, maintenance, expiration, reconciliation, or backup settings.
   at Microsoft.Extensions.Options.OptionsFactory`1.Create(String name)
   at Program.<Main>$(String[] args) in C:\Users\Ochenstarik\projects\smm-antigravity\src\ServerMonitorManager.Control\Program.cs:line 127
```

Result: Startup validation cleanly catches values out of range [1..90] on self-contained trimmed binary.
