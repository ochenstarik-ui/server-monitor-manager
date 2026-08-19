# REPORT — Server Monitor Manager v0.1.0-alpha.9

Дата выпуска: 2026-08-10  
Релиз: https://github.com/ochenstarik-ui/server-monitor-manager/releases/tag/v0.1.0-alpha.9  
Неизменяемый tag target: `2ca059f0b31e2dcc7ce7173f9a487dfd2c5023ad`  
Статус: **выпущен и проверен**.

## Выполненные изменения

- Monitor snapshot приведён к единому контракту из 18 `KEY=value` полей.
- Добавлены `SYSTEMD_SSH` и `SYSTEMD_WIREGUARD`; удалён внеконтрактный `MESH STATUS`.
- Shell producer и Desktop consumer проверяются одним `tests/contracts/monitor-snapshot-v1.txt`.
- Production `SshMonitorService.cs` в итоговой реализации не менялся.
- Добавлен постоянный fixture БД alpha.7, созданный на SQLitePCLRaw `2.1.12`; текущая версия использует `3.0.5`.
- Проверены agents, identities, links, provisioning, audit, `backup-create` и `backup-restore` без повторного применения миграций и потери данных.
- Добавлена runtime-проверка self-contained trimmed single-file Control для `linux-arm64`.
- `deploy/smm-setup.sh` стал отслеживаемым source-of-truth; workaround `validate_control_url` отсутствует.
- Закреплена неизменяемость опубликованных тегов и ассетов.
- Устранены два конкурирующих release publisher: `.github/workflows/linux-release.yml` является единственным издателем GitHub Release; Windows workflow оставлен только для ручной упаковки.

## Интеграция

- Основной PR: https://github.com/ochenstarik-ui/server-monitor-manager/pull/32 — merged как `d0a46f511b2dc9037dc13fdf543eca77fc0758b9`.
- Single-writer PR: https://github.com/ochenstarik-ui/server-monitor-manager/pull/33 — merged как `2ca059f0b31e2dcc7ce7173f9a487dfd2c5023ad`.
- PR #31 ранее закрыт без merge; его требуемое функциональное изменение уже присутствовало в `main`.
- Оба независимых read-only review завершились `VERDICT=PASS`.

## Intentional-red evidence

В отдельном proof commit `28916b01fa3dd1859f3879c43f2b33639a6c7c0a` Desktop-ключ временно переименован `MEM_TOTAL_KB → MEM_TOTAL`.

Красный CI: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31379098394

Результат: ожидается `MEM_TOTAL_KB`, получен `MEM_TOTAL`; 119 тестов прошли, 1 упал. Proof commit не входит в итоговый tag.

## Релиз

Release pipeline: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/31381270348

Успешные jobs:

- Windows package;
- Linux x64 publish;
- Linux arm64 publish;
- bootstrap;
- manifest/sign/publish.

После публикации скачаны все 16 assets. Проверены:

- SHA-256 каждого файла против GitHub asset digest;
- все 8 digest из `server-monitor-manager-manifest.json`;
- standalone sidecars и Windows `SHA256SUMS`;
- manifest version `v0.1.0-alpha.9`;
- tag target совпадает с финальным `main`;
- Cosign-подпись manifest: `Verified OK`;
- сертификат и signature сопоставлены с Rekor entry `108e9186e8c5677aebee891d192fd0a9dedbbcdaf949f9cb74a4b342af33925561ca275e32c114c8`;
- certificate identity: `https://github.com/ochenstarik-ui/server-monitor-manager/.github/workflows/linux-release.yml@refs/tags/v0.1.0-alpha.9`;
- OIDC issuer: `https://token.actions.githubusercontent.com`.

## Ограничения

Локальный компьютер — Windows/x64, поэтому локальный native ARM64 runtime не запускался. ARM64 runtime подтверждён в CI через self-contained/QEMU acceptance и native ARM64 Ubuntu jobs. Это отдельно от локальных проверок отражено в `TEST_EVIDENCE.md`.
