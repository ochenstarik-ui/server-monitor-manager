# Обновление зависимостей: GitHub Actions и NuGet

- **Кому:** `claude`
- **Дата:** 2026-08-18
- **От кого:** владелец проекта (задание получено в чате: «актуализируй проект»,
  уточнено выбором «Обновить зависимости»)
- **Ветка:** `claude/deps-update`
- **Файл отчёта:** `../done/2026-08-18-deps-update.md`

## Что нужно сделать

Закрыть отставание зависимостей:

1. GitHub Actions — три открытых PR от Dependabot (#44 `docker/setup-qemu-action`
   3.7.0 → 4.2.0, #45 `sigstore/cosign-installer` 3.5.0 → 4.1.2,
   #46 `actions/download-artifact` 4.1.8 → 8.0.1).
2. NuGet — версии в `Directory.Packages.props` и `Directory.Build.props`
   против актуальных на nuget.org.

## Границы

Не мержить PR Dependabot и не пушить ветку без подтверждения владельца.
Не менять способ закрепления действий (tag → SHA) — это отдельная задача,
даже если несоответствие очевидно. Не править код проекта, документацию
и CHANGELOG.

## Как проверить результат

- Обновлённые SHA в `.github/workflows/` совпадают с SHA из соответствующих
  веток Dependabot: `git diff origin/main...origin/dependabot/...`;
- `grep -rn "uses:" .github/workflows/` — в дереве не осталось старых версий;
- для NuGet: `dotnet list package --outdated` и восстановление
  `packages.lock.json` (`RestorePackagesWithLockFile=true`,
  в CI `-p:RestoreLockedMode=true`).

## Контекст и ограничения

Релизная подпись зависит от cosign: `deploy/ochenstarik-server-monitor-manager.sh`
закрепляет потребительский `COSIGN_VERSION="v3.1.3"` с проверкой SHA-256,
а CI ставит cosign через `sigstore/cosign-installer` без закрепления версии
cosign. `docs/release-policy.md` (alpha.17, alpha.18) описывает, что контракт
producer/consumer по cosign уже ломался. Смена версии installer — решение
владельца, не самостоятельное.

Наличие .NET SDK на рабочей машине на момент постановки задачи не определено.
