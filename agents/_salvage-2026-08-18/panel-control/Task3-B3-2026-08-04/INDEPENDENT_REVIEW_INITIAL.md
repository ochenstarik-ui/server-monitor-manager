# VERDICT: **REQUEST_CHANGES**

**Количество замечаний:** **6**

- **BLOCKING:** 2
- **HIGH:** 2
- **MEDIUM:** 1
- **LOW:** 1

Прочитан полный authoritative B-3 spec — 163 строки. Проверен весь diff от `b11c277ac7f79a18670932eca4622982d9ff48e0`: **23 пути, +1043 / −125**, а также неизменённые пути создания/резервирования mesh Node, reconnect, TTL, reenrollment, helper/sudo и CI.

---

## BLOCKING

### B1. DB-less orphan не проверяется фактом; Disabled cleanup может удалить правило, но записать `PendingActivation` и потребить marker

**Файлы:**

- `src/ServerMonitorManager.Control/LinkService.cs:387-455`
- `src/ServerMonitorManager.Control/LinkService.cs:266-286`
- `deploy/ochenstarik-smm-policy-apply:122-138`
- `deploy/ochenstarik-smm-policy-apply:173-184`
- `src/ServerMonitorManager.Control/LinkReconciliationBackgroundService.cs:74-84`

**Проблема:**

1. Для DB-less orphan (`persisted:false`) выполняется raw `link-disconnect`, но блок factual verification намеренно пропускается:

   ```csharp
   if (persisted)
   {
       await VerifyFactualStateAsync(...);
   }
   ```

   После удаления код просто присваивает синтетическому объекту `ActualState=Disabled`. Нет второго `link-list` и нет доказательства, что все handles действительно исчезли.

2. Для persisted `Disabled` правило удаляется raw helper-операцией, после чего `VerifyFactualStateAsync` вызывает `link-status`. Но `link-status` требует обе записи в `nodes.tsv`. Если Node отсутствует, helper возвращает exit 80 уже **после успешного удаления правила**. `ConvergeAsync` ловит это и записывает:

   ```text
   ActualState=PendingActivation
   LastError=mesh.node-not-activated
   ```

3. `PendingActivation` не учитывается ни как `Failed`, ни как `Converged` в `ReconcileAllAsync`. Поэтому возможен результат:

   ```text
   Examined=1, Converged=0, Failed=0
   ```

   Фоновый сервис видит `Failed == 0` и потребляет marker.

Это нарушает требования:

- exact post-mutation factual verification;
- удаление всех Disabled/no-DB duplicates;
- корректную семантику `Examined/Converged/Failed`;
- marker не должен считаться успешно завершённым при неклассифицированном результате.

**Исправление:**

- После каждой mutation сверять факт повторным `link-list`, а не `link-status`.
- Для `Active` требовать **ровно одну** запись.
- Для `Disabled` и DB-less orphan требовать **ноль** записей.
- Не делать factual verification удаления зависимой от наличия Node в `nodes.tsv`.
- Добавить инвариант результата: каждый examined элемент должен попасть ровно в `Converged`, `Failed` либо явно типизированное ожидаемое состояние, которое фоновой сервис обрабатывает сознательно.
- Добавить тесты:
  - DB-less disconnect сообщает успех, но оставляет правило;
  - persisted Disabled с отсутствующим Node;
  - marker не потребляется при не подтверждённом factual результате.

---

### B2. B3-6 не работает для реального зарезервированного, но ещё не активированного Node

**Файлы:**

- `deploy/ochenstarik-smm-policy-apply:37-44`
- `deploy/ochenstarik-server-monitor-manager.sh:375-389`
- `deploy/ochenstarik-server-monitor-manager.sh:852-883`
- `tests/bootstrap/test-bootstrap-contract.sh` — новый inactive-node fixture

**Проблема:**

`reserve_node_address` записывает ещё не активированный Node так:

```text
node-id<TAB>10.77.0.x<TAB>-<TAB>reserved
```

`peer-add` позже меняет четвёртое поле на `active`.

Новый `lookup_node_ip` проверяет только существование строки и валидность второго поля — IP. Поля public key/status не читаются. Поэтому реальная строка со статусом `reserved` принимается как активированная, и `link-connect` создаёт nft accept-rule вместо exit 80.

Новый тест моделирует другой случай — полное отсутствие строки — и потому не покрывает фактическую топологию.

Я воспроизвёл это напрямую с production-shaped fixture:

```text
source  10.77.0.2  key-source  active
target  10.77.0.3  -           reserved
```

Фактический результат helper:

```text
EXIT=0
nft add rule inet ochenstarik_smm links ip saddr 10.77.0.2 ip daddr 10.77.0.3 tcp dport 22 counter accept comment smm:source:target:tcp:22
```

Ожидался exit 80 / `mesh.node-not-activated`.

**Исправление:**

- `lookup_node_ip` должен разбирать как минимум address и status.
- `status != active` должен давать exit 80 с точным `mesh.node-not-activated`.
- Повреждённая строка `active` с пустым/невалидным IP должна оставаться exit 78.
- Тестировать настоящий формат:
  - `reserved` + валидный IP → exit 80;
  - `active` + валидный IP → success;
  - `active` + невалидный IP → exit 78.

---

## HIGH

### H1. Reflection-style сериализация orphan audit не имеет доказанной безопасности в trimmed runtime

**Файл:** `src/ServerMonitorManager.Control/ControlStore.cs:1376-1398`

Используется:

```csharp
JsonSerializer.Serialize(new
{
    rule.SourceNodeId,
    rule.TargetNodeId,
    rule.Protocol,
    rule.Port
})
```

Остальной Control последовательно передаёт source-generated `JsonTypeInfo`. Отсутствие trim warnings не доказывает, что этот путь успешно выполнится в опубликованном trimmed artifact. Сам orphan execution path в опубликованном бинарнике не запускался.

Если reflection metadata/resolver недоступны, исключение возникнет **после firewall mutation**. К этому моменту `link.orphan-removed` уже опубликован, затем общий `catch` классифицирует операцию как `Partial` и публикует failure event. Получится противоречивая телеметрия: правило фактически могло быть удалено, orphan event уже отправлен, но pass считается failed.

**Ответ на uncertainty №2:** текущего доказательства недостаточно; реализация не соответствует принятой source-generated convention и должна считаться небезопасной для trimmed delivery до runtime-проверки.

**Исправление:**

- Ввести именованный audit DTO.
- Добавить его в source-generated JSON context в Control assembly.
- Вызывать `JsonSerializer.Serialize(value, Context.Default.TypeInfo)`.
- Запустить именно опубликованный `linux-x64 PublishTrimmed` artifact через orphan-removal path и проверить содержимое audit.
- Лучше записывать audit до публикации финального success event либо явно определить атомарную последовательность failure handling.

---

### H2. Linux helper integration test остался на старом `link-status`-first протоколе и должен падать в Linux CI

**Файл:** `tests/ServerMonitorManager.Control.Tests/LinkPolicyApplierIntegrationTests.cs:45-72,89-106,139-147`

Fake helper не реализует `link-list`. Ожидаемый invocation log также начинается с:

```text
link-status
link-connect
link-status
```

Но новый `ReconcileAllAsync` всегда сначала вызывает `link-list`.

На Windows тест возвращается на `OperatingSystem.IsLinux()==false`, поэтому заявленные **85/85** не выявляют дефект. Workflow `.github/workflows/linux-control-agent.yml` выполняет Control tests на Ubuntu, где этот тест не будет пропущен.

Следовательно, DoD «CI зелёный на PR» не подтверждён, а тест реального typed process boundary устарел.

**Исправление:**

- Реализовать `link-list` в fake helper.
- Обновить expected log на новый факт-первичный протокол.
- Проверять:
  - no-drift: только один `link-list`;
  - missing table классифицируется непосредственно на `link-list`, без промежуточной mutation;
  - mutation сопровождается factual post-list verification;
  - DB-less raw disconnect.
- Выполнить Linux Control suite, а не только Windows run.

---

## MEDIUM

### M1. Default `ILinkPolicyApplier.ListRulesAsync` fail-open возвращает пустую фактическую конфигурацию

**Файл:** `src/ServerMonitorManager.Control/LinkPolicyApplier.cs:7-14`

```csharp
Task<IReadOnlyList<LinkRule>> ListRulesAsync(...)
    => Task.FromResult<IReadOnlyList<LinkRule>>([]);
```

Любая забытая реализация интерфейса молча сообщает «firewall пуст», а не падает. Для источника факта это небезопасный default и уже позволил старым fakes компилироваться без реализации нового обязательного протокола.

**Исправление:**

- Сделать `ListRulesAsync` и raw `ApplyDisconnectAsync(LinkRule, ...)` обязательными abstract interface members.
- Обновить все test doubles явно.
- Не использовать пустой набор как compatibility fallback.

---

## LOW

### L1. Новые вложенные lock scopes существенно ухудшили читаемость

**Файл:** `src/ServerMonitorManager.Control/LinkService.cs:181-229,240-293`

`try` bodies на строках примерно 192 и 249 визуально находятся на уровне внешнего scope. При двух проходах — persisted candidates и factual orphans — это затрудняет проверку порядка:

```text
sorted node locks → selected current tuple → current per-Link gate
```

Семантической смены порядка блокировок я не обнаружил, новых lock-классов нет. В нормальном success path одинаковые события не дублируются. Противоречивые success/failure events возможны только через проблему H1.

**Исправление:** переформатировать вложенные scopes либо вынести обработку одного tuple/orphan в небольшие приватные методы без копирования convergence logic.

---

# Сопоставление со спецификацией

| Раздел | Статус | Вывод |
|---|---|---|
| **B3-1** | В основном PASS | Строгая арность, managed-comment parser, foreign ignore, stable TSV, exit 78/79, explicit `/usr/sbin/nft`, testing mode реализованы. |
| **B3-2** | **FAIL** | Факт-первичный проход и newest tuple есть, no-drift стоит один вызов, duplicates обрабатываются; DB-less factual verification отсутствует, Disabled verification зависит от `nodes.tsv`. |
| **B3-3** | PASS | Три prompt attempt, single warning, regular throttle restore, marker retention, firewall backoff и generation completion сохранены. |
| **B3-4** | PASS с оговоркой B1 | `Examined/Converged/Failed` разведены и consumers обновлены, но `PendingActivation` после Disabled cleanup может не попасть ни в один итоговый класс. |
| **B3-5** | PASS | Default effective+drift filter, history toggle, displayed counters, configurable retention; newest Disabled tuple удаляется вместе с историей, старый Active не воскресает. |
| **B3-6** | **FAIL** | Реальный `reserved` row ошибочно считается активированным. |
| **B3-7** | PASS | Missing mesh directory, explicit nft executable check и acceptance formatting исправлены. |

## Обязательные тесты / DoD

Покрытие добавлено для Disabled/Disabled, no-DB orphan, one-list no-drift, foreign rule, duplicates, marker cap, firewall 79 и retention. Однако:

- нет real-shape теста `status=reserved`;
- нет негативного post-mutation factual verification;
- Linux process integration test устарел;
- физический acceptance не выполнялся;
- CI green не подтверждён.

---

# Security / trust boundary verdict

**Условно PASS на injection/isolation, FAIL на protocol correctness.**

Подтверждено:

- helper actions имеют строгую арность;
- `link-list` проверяется до общего action parser;
- foreign comments игнорируются;
- удаления ограничены точным managed comment;
- forged `smm:` comments диагностируются и не выводятся;
- exit 78/79/80 распознаются только в сочетании с точным marker;
- используется фиксированный `/usr/sbin/nft`;
- `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`, shell interpolation отсутствует;
- sudoers и существующие peer/SSH boundaries не расширялись;
- неизвестные helper/nft ошибки остаются fail-closed;
- порядок node locks → per-Link gate сохранён, новых lock-классов нет;
- create/orphan adoption защищены повторным чтением effective tuple под node locks;
- mid-pass firewall unavailable останавливает последующие candidates и агрегирует событие.

Но exit 80 сейчас не соответствует реальному состоянию `reserved`, а raw orphan protocol не подтверждает factual result. Поэтому security-sensitive kill-switch convergence пока нельзя одобрить.

---

# Фактические команды и evidence

Успешно выполнено:

- `git diff --check b11c277...` — PASS
- `bash -n deploy/ochenstarik-smm-policy-apply tests/bootstrap/test-bootstrap-contract.sh tests/acceptance/three-server-mesh.sh` — PASS
- `bash tests/bootstrap/test-bootstrap-contract.sh` — `BOOTSTRAP_CONTRACT=PASS`
- `powershell.exe ... tests/windows/Test-DesktopContracts.ps1` — `Windows desktop contracts passed`
- ad-hoc real-shaped `reserved` Node helper probe — **EXIT=0 и nft add rule**, подтвердил BLOCKING B2
- финальный `git status --short` и diff numstat — те же 23 изменённых пути, **+1043/−125**

Локально не удалось независимо повторить:

- Control 85/85;
- Desktop Release build;
- trimmed Linux publish.

Причина: `dotnet` отсутствует и в Bash PATH, и в Windows PowerShell PATH текущего review environment. Поэтому заявленные worker evidence не считаю независимо воспроизведёнными. Особенно важно повторить Linux suite после исправления H2.

---

# Physical acceptance residual

Physical topology inputs отсутствуют. Harness содержит restore injection и остаётся готов к запуску, но фактический:

```bash
SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh
```

не выполнялся. Статус может быть только **verified candidate / physical acceptance pending**, не «выполнено».

---

## Итог

- **Что сделано:** полный read-only review spec, diff, unchanged call paths, security/concurrency boundaries и доступных deterministic checks.
- **Главные результаты:** найдены два блокирующих дефекта — отсутствие exact factual verification удаления и неработающая семантика реального `reserved` Node.
- **Файлы:** не создавал и не изменял.
- **Ограничение:** .NET toolchain недоступен в review environment; Linux/trimmed evidence требует повторного запуска после исправлений.