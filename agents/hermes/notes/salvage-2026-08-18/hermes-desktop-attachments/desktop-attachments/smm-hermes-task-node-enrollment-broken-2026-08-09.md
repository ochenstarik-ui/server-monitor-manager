# Путь регистрации Node не работает от начала до конца

Приоритет: **блокирующий**. Найдено при первой в истории проекта установке на живые серверы 2026-08-09. Все четыре дефекта лежат на пути, который CI не проходит ни разу.

## Репозиторий

- https://github.com/ochenstarik-ui/server-monitor-manager
- База: `main` @ `d645812` (= тег `v0.1.0-alpha.7`, из которого собран проверявшийся релиз)
- Ветка: `hermes/node-enrollment-fix`

## Контекст

Hub ставится и работает: Control активен, `/healthz` отвечает `Healthy`, WireGuard `smm0` поднят, таблица `inet ochenstarik_smm` создана. Дальше не проходит ничего.

---

## Дефект 1 — конфигурация Agent не читается в опубликованном бинарнике (BLOCKER)

### Наблюдение

```
$ sudo -u ochenstarik-smm-agent env SMM_NodeId='INVALID_UPPER' \
    /usr/local/lib/ochenstarik-server-monitor-manager/agent/ochenstarik-smm-agent
Unhandled exception. System.IO.FileNotFoundException:
  Could not find file '/var/lib/ochenstarik-server-monitor-manager/agent/agent.pfx'
```

Заведомо невалидный `SMM_NodeId` **не вызвал** ошибку валидации из `Program.cs`. Значит `configuration.Get<AgentOptions>()` вернул объект с умолчаниями, а все переменные окружения проигнорированы.

Дефект не виден снаружи, потому что умолчания валидны: `NodeId` по умолчанию — имя машины, числовые настройки в допустимых диапазонах. Единственное свойство без валидного умолчания — `EnrollTokenFile` (`null`), поэтому `Program.cs` не заходит в ветку регистрации, падает в `RunAsync` и ищет несуществующий `agent.pfx`.

### Причина

Публикация идёт с `-p:PublishTrimmed=true`. Рефлексивная привязка конфигурации теряет метаданные свойств, вырезанные линкером.

Правильное решение в проекте **уже применено к Control**:

```csharp
// ControlOptions.cs
[DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ControlOptions))]
public ControlOptions() { }
```

В `AgentOptions` такого атрибута нет. Поэтому Control конфигурацию читает, а Agent — нет.

### Что сделать

Не ограничиваться копированием атрибута. Предпочтительно — включить source-generated биндер, он trim-safe по построению и снимает целый класс таких отказов:

```xml
<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>
```

в `ServerMonitorManager.Agent.csproj` и `ServerMonitorManager.Control.csproj`. Атрибут `DynamicDependency` в `ControlOptions` после этого можно оставить как страховку, но он перестаёт быть единственной защитой.

Отдельно: **при неудачной привязке Agent обязан отказываться стартовать, а не тихо использовать умолчания.** Добавить явную проверку — если `SMM_ControlUrl` или `SMM_NodeId` присутствуют в окружении, но не попали в `options`, это фатальная ошибка конфигурации с внятным сообщением. Молчаливый откат на умолчания в компоненте, который держит mTLS-идентичность, недопустим.

---

## Дефект 2 — `validate_control_url` не пропускает ни один адрес (BLOCKER)

### Наблюдение

```
$ sudo ochenstarik-server-monitor-manager.sh node-code ai-agent
ERROR: Control URL must be an https URL without a path or credentials.
```

`/etc/ochenstarik-server-monitor-manager/control-public-url` содержал корректный `https://host1885995-3.hostland.pro:7443`.

### Причина

```bash
[[ "$1" =~ ^https://[A-Za-z0-9._:\[\]-]+(:[0-9]{1,5})?/?$ ]]
```

Внутри скобочного выражения POSIX обратный слэш не экранирует. Класс закрывается на первом `]`, а хвост `-]` становится обязательным литералом после хоста. Проверено: не совпадает даже `https://example.com`.

### Что сделать

```bash
[[ "$1" =~ ^https://[]A-Za-z0-9._:[-]+(:[0-9]{1,5})?/?$ ]]
```

`]` первым символом класса, `[` и `-` — последними. Проверено: принимает `https://host:7443`, `https://example.com`, `https://10.0.0.1:7443`; отклоняет `https://a.b/path` и `http://x.y`.

Функция вызывается в `create_node_code` и в `install_agent` — то есть дефект блокирует оба пути регистрации.

**Обязательно**: юнит-тест на `validate_control_url` в `tests/bootstrap/test-bootstrap-contract.sh` с набором принимаемых и отклоняемых адресов. Аналогично проверить остальные regexp с классами символов в bootstrap и helper — эта ошибка воспроизводится копипастом.

---

## Дефект 3 — `control-device-code` не существует

`tests/acceptance/three-server-mesh.sh:117` вызывает:

```bash
device_code="$(hub_ssh "$INSTALLER_COMMAND control-device-code '$CONTROL_DEVICE_ID'" …)"
```

В bootstrap девятнадцать действий, `control-device-code` среди них нет. В `docs/installer-contract.md` §5 оно значится как целевое, в Control есть CLI `device-token-create` — связки между ними никто не написал.

Приёмка падает на шаге 2 из 12, до всего остального.

**Что сделать:** добавить действие `control-device-code DEVICE_ID`, выдающее код `SMMDEV1-...` по образцу `node-code`, через существующий `run_control_cli device-token-create`.

---

## Дефект 4 — `INSTALLER_COMMAND` указывает в пустоту

```bash
INSTALLER_COMMAND="${INSTALLER_COMMAND:-sudo /usr/local/sbin/ochenstarik-server-monitor-manager.sh}"
```

Bootstrap кладёт в `/usr/local/sbin/` только `ochenstarik-smm-emergency`, себя — нет.

**Что сделать:** устанавливать bootstrap в `/usr/local/sbin/ochenstarik-server-monitor-manager.sh` при `install-control` и `install-agent` — это же соответствует `docs/installer-contract.md` §5, где CLI описан как системная команда. Либо изменить умолчание в скрипте приёмки. Первое лучше: документация уже обещает системную команду.

---

## Дефект 5 — CI не проверяет путь регистрации

Первые два дефекта — блокирующие, лежали в репозитории неделями и пережили сотню зелёных прогонов. Причина одна: **CI ни разу не запускает Agent из опубликованного релизного бинарника.**

`linux-control-agent.yml` ставит только Control и дёргает `/healthz`. `linux-platform-matrix.yml` — то же самое. Публикация Agent выполняется, но получившийся бинарник только складывается в артефакт и никогда не исполняется.

**Что сделать:** расширить systemd-smoke до полного цикла на одной машине:

1. `install-control`;
2. `node-code` — проверяет дефект 2;
3. `install-agent` с полученным кодом — проверяет дефект 1, запускает **опубликованный trimmed бинарник**;
4. проверить, что сертификат Agent выпущен и `ochenstarik-smm-agent.service` активен;
5. `control-device-code` — проверяет дефект 3.

Hub и Node на одной машине для этого допустимы: WireGuard не нужен, mesh не поднимается, проверяется только регистрация. Полноценный mesh по-прежнему требует физической приёмки.

Без этого шага любой из четырёх дефектов вернётся незамеченным.

---

## Критерий приёмки

- `SMM_NodeId='INVALID_UPPER'` на опубликованном trimmed бинарнике даёт ошибку валидации, а не падение на `agent.pfx`;
- `install-node` по коду `SMMNODE2` доводит Node до активного `ochenstarik-smm-agent.service` и выданного сертификата;
- `validate_control_url` принимает и отклоняет адреса по тесту в контрактном скрипте;
- `control-device-code` существует и выдаёт `SMMDEV1-...`;
- bootstrap доступен как `/usr/local/sbin/ochenstarik-server-monitor-manager.sh`;
- CI выполняет полный цикл регистрации на опубликованных бинарниках;
- Control suite прогнан на Linux;
- PR создан, CI зелёный.

## После merge

Выпустить `v0.1.0-alpha.8` и повторить установку на живых серверах. Hub переустанавливать не нужно — он работает; достаточно `update-control` и заново пройти регистрацию Node.

## Временный обход, применённый вручную

На Hub и в установщике `smm-setup.sh` дефект 2 обойдён заплаткой:

```bash
perl -pi -e 's/\Q[A-Za-z0-9._:\[\]-]\E/[]A-Za-z0-9._:[-]/' <bootstrap>
```

После merge обход из `smm-setup.sh` убрать — он помечен комментарием в коде.
