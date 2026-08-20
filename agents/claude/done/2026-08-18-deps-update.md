# Отчёт: обновление зависимостей GitHub Actions и NuGet

- **Задание:** `../inbox/2026-08-18-deps-update.md`
- **Агент:** `claude`
- **Дата:** 2026-08-18
- **Ветка / коммиты:** `claude/deps-update`. Правок workflow эта ветка не
  несёт — см. «Как доставлено».

## Что сделано

Разобрано отставание зависимостей и проверены три открытых PR Dependabot по
GitHub Actions. Проверка была содержательной, а не сводилась к «CI зелёный».

- `actions/download-artifact` v4.1.8 → v8.0.1 (PR #46). Затрагивает одну
  точку — `linux-release.yml:256`; это была последняя старая ссылка в дереве,
  `linux-platform-matrix.yml` уже использовал v8.0.1 (SHA `3e5f45b2…`).
  Обновление приводит репозиторий к одной версии.
- `docker/setup-qemu-action` → v4.2.0 (PR #44). Одна точка,
  `linux-control-agent.yml:83`; прежняя ссылка была закреплена по SHA
  `c7c53464…` с комментарием `# v3` без минорной версии.
- `sigstore/cosign-installer` v3.5.0 → v4.1.2 (PR #45). **Не принят**,
  причина в разделе «Вопросы к приёмке».

Все три правки я предварительно применял локально и построчно сверял с
ветками Dependabot — совпадение полное. Затем правки из ветки сняты, чтобы
не применить одно и то же дважды: доставка идёт слиянием PR Dependabot.

NuGet не обновлялся — см. «Что не сделано».

## Как доставлено

Решением владельца от 2026-08-18: сливаются PR #44 и #46, правки из этой
ветки сняты. В ветке остаются только задания, отчёты и записка передачи
состояния. PR #45 остаётся открытым.

## Как проверено

Сверка применённых правок с ветками Dependabot (до снятия правок):

```
$ git diff -- .github/ | grep -E '^[+-][^+-]' | sort > mine.lines
$ for b in dependabot/github_actions/actions/download-artifact-8.0.1 \
           dependabot/github_actions/sigstore/cosign-installer-4.1.2 \
           dependabot/github_actions/docker/setup-qemu-action-4.2.0; do
      git diff origin/main...origin/$b -- .github/
  done | grep -E '^[+-][^+-]' | sort > pr.lines
$ diff pr.lines mine.lines
СОВПАДАЕТ построчно
```

Старых версий после применения в дереве не оставалось:

```
$ grep -rn "download-artifact@fa0a91b8\|cosign-installer@v3\|setup-qemu-action@c7c53464" .github/workflows/
старых версий не осталось
```

YAML всех шести workflow разбирается:

```
$ for f in .github/workflows/*.yml; do python -c "import sys,yaml;yaml.safe_load(open(sys.argv[1],encoding='utf-8'));print('OK',sys.argv[1])" "$f"; done
OK .github/workflows/linux-control-agent.yml
OK .github/workflows/linux-platform-matrix.yml
OK .github/workflows/linux-release.yml
OK .github/workflows/release-verification.yml
OK .github/workflows/windows-build.yml
OK .github/workflows/windows-release.yml
```

Текущее состояние ветки — правок workflow нет:

```
$ git diff 23eb3f5 --stat -- .github/
(пусто)
```

## Что не сделано

**NuGet не обновлён — нет .NET SDK на машине.**

```
$ dotnet --version
bash: dotnet: command not found

PS> Get-Command dotnet
dotnet not found on PATH
```

В `Directory.Build.props` включён `RestorePackagesWithLockFile=true`, в CI
сборка идёт с `-p:RestoreLockedMode=true`, и в дереве семь
`packages.lock.json`. Поднять версию в `Directory.Packages.props` без
пересборки lock-файлов — гарантированно уронить CI. Без SDK пересобрать их
нечем, поэтому файлы не тронуты.

Отставание по данным `api.nuget.org` на 2026-08-18 (запрошены версии,
`dotnet list package --outdated` не запускался):

| Пакет | В репозитории | Актуальная |
|-------|---------------|------------|
| Microsoft.AspNetCore.Authentication.Certificate | 10.0.10 | 10.0.11 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.10 | 10.0.11 |
| Microsoft.Data.Sqlite | 10.0.10 | 10.0.11 |
| Microsoft.Extensions.Configuration.* (4 пакета) | 10.0.10 | 10.0.11 |
| System.Security.Cryptography.ProtectedData | 10.0.0 | 10.0.11 |
| Microsoft.NET.ILLink.Tasks (`Directory.Build.props`) | 10.0.10 | 10.0.11 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2270 | 10.0.28000.2526 |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.4.0 | 0.6.0 |
| Microsoft.WindowsAppSDK | 2.2.0 | 2.4.0 |
| Microsoft.NET.Test.Sdk | 18.8.1 | 18.9.0 |
| xunit.v3 | 3.2.2 | 4.0.0 (мажорная) |
| xunit.runner.visualstudio | 3.1.5 | 4.0.0 (мажорная) |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | 3.0.5 — актуален |

Открытых PR Dependabot по NuGet нет, хотя в `.github/dependabot.yml`
экосистема `nuget` настроена с недельным интервалом. Причина не определена.

## Замечено рядом

Вне границ задачи; предлагаю отдельными заданиями.

1. **`sigstore/cosign-installer` закреплён тегом, а не SHA.** Все остальные
   действия в репозитории закреплены по коммиту с комментарием версии;
   cosign-installer — единственное исключение, и это единственное действие,
   от которого зависит подпись релиза. Тег подвижен.
2. **`Microsoft.Windows.SDK.BuildTools.WinApp` 0.4.0** — версия ниже 1.0,
   мажорных гарантий совместимости нет; переход на 0.6.0 делать отдельно.
3. **xunit 3 → 4 — мажорный переход** для двух пакетов, отдельной задачей
   с прогоном тестов.

## Вопросы к приёмке

**PR #45 (`cosign-installer` 3.5.0 → 4.1.2) не принят к слиянию.**

Зелёные проверки на #45 и #46 про релиз ничего не доказывают:
`linux-release.yml` запускается только по `push` тега `v*` и по
`workflow_dispatch` (строки 3–12), на pull request он не выполняется вовсе.
Мажорная смена installer меняет версию cosign у producer, а
`deploy/ochenstarik-server-monitor-manager.sh:34` фиксирует у consumer
`COSIGN_VERSION="v3.1.3"` с проверкой SHA-256. Какую версию cosign ставит
installer 4.1.2 по умолчанию — не проверял, это не определено.
`docs/release-policy.md` показывает четыре поломки контракта
producer/consumer по cosign: alpha.12, alpha.13, alpha.17, alpha.18.

Предлагаю до слияния #45 прогнать `Release pipeline` через
`workflow_dispatch` на репетиционном теге — с оговоркой из
`agents/claude/notes/2026-08-18-передача-состояния.md`: репетиция идёт по
`refs/heads/main` и пропускает шаги под условием `refs/tags/*`, так был
сожжён alpha.19. Значит проверять нужно ещё и то, что нужные шаги вообще
исполнялись.

Второй вопрос: обновлять ли NuGet отдельным заданием на машине с .NET SDK.
