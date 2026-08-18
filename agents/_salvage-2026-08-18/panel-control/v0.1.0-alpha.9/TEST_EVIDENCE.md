# TEST_EVIDENCE — v0.1.0-alpha.9

## Локальные проверки

| Проверка | Результат |
|---|---|
| Monitor producer contract | PASS |
| Shared Desktop field-name contract | PASS |
| Previous-schema CLI contract | PASS |
| Release contract | PASS |
| Bootstrap contract | PASS |
| Control test suite | 120/120 PASS |
| `dotnet format whitespace` | PASS |
| `dotnet format style` | PASS |
| `git diff --check` | PASS |
| Static security scan | PASS |
| SQLite fixture integrity | `PRAGMA integrity_check = ok` |
| Fixture SHA-256 | `15bf788dd5789a55bd54a4a548d339b4e29e54e1c75311b21eec079e0ef2faa2` |
| Fixture schema version | `PRAGMA user_version = 8` |
| Independent implementation review | PASS |
| Independent single-writer review | PASS |

Полная сборка завершилась без ошибок. В неизменённых файлах присутствовали 40 существующих analyzer warnings; текущий diff не добавлял эти предупреждения.

## Intentional-red

- Commit: `28916b01fa3dd1859f3879c43f2b33639a6c7c0a`
- Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31379098394
- Expected failure: `MEM_TOTAL_KB` против `MEM_TOTAL`.
- Результат: 119 passed / 1 failed.
- В финальном tag намеренная поломка отсутствует.

## CI основного PR #32

- Linux control and agent: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31378990081 — SUCCESS
- Linux platform matrix: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31378989933 — SUCCESS
- Windows build: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31378989978 — SUCCESS

## CI single-writer PR #33

- Linux control and agent: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31380368303 — SUCCESS
- Linux platform matrix: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31380368307 — SUCCESS
- Windows build: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31380368308 — SUCCESS

## Финальный `main`

Commit: `2ca059f0b31e2dcc7ce7173f9a487dfd2c5023ad`

- Linux control and agent: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31380793033 — SUCCESS
- Linux platform matrix: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31380792955 — SUCCESS
- Windows build: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31380793000 — SUCCESS

Linux matrix включает успешные Ubuntu 22.04/24.04 x64 и ARM64, а также Debian 12/13 x64 и ARM64 systemd restart tests.

## Release CI

- Pipeline: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31381270348 — SUCCESS
- `package-windows`: SUCCESS
- `publish-linux (linux-x64)`: SUCCESS
- `publish-linux (linux-arm64)`: SUCCESS
- `bootstrap`: SUCCESS
- `manifest`: SUCCESS

## Проверка опубликованных assets

| Проверка | Результат |
|---|---|
| Скачано assets | 16/16 |
| GitHub asset SHA-256 digests | PASS |
| Manifest hashes | 8/8 PASS |
| Sidecar checksums | PASS |
| Tag target = final main | PASS |
| Cosign manifest signature | `Verified OK` |
| Rekor signature correspondence | PASS |

## Не запущено локально

- Native `linux-arm64` executable не запускался непосредственно на локальном Windows/x64 host: подходящего native ARM64 host нет.
- Вместо этого runtime выполнен в Linux CI, включая self-contained/QEMU acceptance и native ARM64 VM jobs; это не подменено одним успешным publish.
