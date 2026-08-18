# Antigravity: жизненный цикл клиентских сертификатов

## Сначала — слить открытые PR

Три PR зелёные и ждут merge. Слить **до** начала новой работы, иначе всё придётся ребейзить дважды: `main` уехал на `d645812` (B-3R), а PR #14 и #15 сделаны от `b11c277`.

Порядок:

1. **#13** — `docs/product-horizons-and-integration`, только документация, конфликтов нет;
2. **#14** — `antigravity/repo-hygiene`;
3. **#15** — `antigravity/reproducible-builds`.

#14 и #15 трогают одни и те же два workflow, но в разных местах: #14 меняет строки `uses:`, #15 — строки `run:` с восстановлением. Автослияние должно пройти; если нет — ребейз #15 поверх #14, а не наоборот.

После merge проверить, что `main` зелёный: обе ветки сделаны от старой базы, а B-3R с тех пор добавил файлы.

## Приёмка PR #15 — принято

Все три дефекта закрыты правильно:

- разделение `dotnet format whitespace` и `dotnet format style` вместо общего вызова, аналайзеры сохранены, подавлений в `.editorconfig` нет — это был предпочтительный из трёх вариантов;
- RID в lock-файлах;
- `-p:RestoreLockedMode=true` на всех семи шагах `publish`, включая release-workflow.

---

## Новая задача

### Репозиторий и ветка

- База: `main` после merge трёх PR выше
- Ветка: `antigravity/cert-lifecycle`
- Рабочая папка: `C:\Users\Ochenstarik\projects\smm-antigravity`

Полная постановка — в `smm-hermes-task-cert-lifecycle-2026-08-06.md`, она остаётся в силе целиком. Ниже только то, что изменилось с 6 августа.

### Зачем это сейчас

Пункт Горизонта 0, который не начинал никто. Hermes занят блокирующим дефектом регистрации Node, а без сертификатов гейт не закрывается всё равно.

`CertificateAuthority.cs` выдаёт клиентские сертификаты сроком на год, автопродления нет ни у Agent, ни у Operator. Через год после развёртывания весь парк одновременно перестаёт проходить mTLS, и восстановление потребует ручной перерегистрации каждого Node одноразовым кодом. Процедуры ротации Control CA в проекте нет вообще.

### Объём

По документу от 6 августа: срок в конфигурации с валидацией, автопродление Agent за треть срока через действующий mTLS-канал, продление Operator-сертификата Desktop, событие `certificate.expiring`, документ `docs/certificate-rotation.md`, семь тестов.

### Разграничение с Hermes — изменилось

Hermes ведёт ветку `hermes/node-enrollment-fix` и трогает:

```
src/ServerMonitorManager.Agent/AgentOptions.cs
src/ServerMonitorManager.Agent/Program.cs
src/ServerMonitorManager.Agent/ServerMonitorManager.Agent.csproj
src/ServerMonitorManager.Control/ServerMonitorManager.Control.csproj
deploy/ochenstarik-server-monitor-manager.sh
tests/acceptance/three-server-mesh.sh
tests/bootstrap/test-bootstrap-contract.sh
.github/workflows/*   (расширение smoke-теста)
```

**Не трогать ничего из этого списка.** В частности: правки в `Agent/Program.cs` для продления сертификата вносить нельзя — если понадобится, описать нужное изменение в `REPORT.md`, Hermes внесёт у себя.

Ваша область: `CertificateAuthority.cs`, `CertificateLifecycleService.cs`, `AgentClient.cs`, `ControlOptions.cs`, `Control/Program.cs`, `appsettings.json`, Desktop, новый `docs/certificate-rotation.md`, тесты сертификатов.

Пересечение по `.csproj` возможно: Hermes добавляет `EnableConfigurationBindingGenerator` в Agent и Control. Вам `.csproj` менять не нужно — если понадобится, согласовать.

### Дополнительное требование, которого не было 6 августа

Сегодня выяснилось, что `AgentOptions` теряет конфигурацию при `PublishTrimmed=true`, потому что в нём нет `[DynamicDependency]`, который есть в `ControlOptions`. Дефект прожил недели и был найден только запуском на живом сервере.

Поэтому: **всё, что вы добавите в конфигурацию, обязано проверяться на опубликованном trimmed-артефакте, а не только в тестах.** Новая настройка срока сертификата — не исключение. В `TEST_EVIDENCE.md` показать, что значение из `control.env` действительно доходит до кода в собранном релизном бинарнике, а не подменяется умолчанием.

Простейшая проверка: задать заведомо невалидное значение и убедиться, что сервис отказывается стартовать с внятной ошибкой валидации. Если стартует — конфигурация не читается.

### Критерий приёмки

Как в документе от 6 августа, плюс:

- новая настройка срока подтверждена на опубликованном `linux-x64 PublishTrimmed` артефакте;
- `git diff --name-only main` не содержит ни одного файла из списка Hermes;
- CI зелёный, PR не смержен.

### Отчёт

Раздельно: локально, в CI со ссылками, не проверялось и почему. Описание PR обновлять **после** завершения CI.
