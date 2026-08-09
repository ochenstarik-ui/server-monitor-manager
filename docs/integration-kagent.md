# Интеграция с KAgent

Статус: спецификация. Ничего из описанного не реализовано. Работы разрешены после закрытия Горизонта 1 — см. [горизонты продукта](product-horizons.md).

## Принцип

KAgent — внешняя AI-система. Она **просит**, Server Monitor Manager **решает и исполняет**. Никакая часть интеграции не даёт KAgent исполнения на хосте: запрос от KAgent порождает обычный typed provisioning job с immutable plan, режимом подтверждения из [политик](approval-policies.md), execution grant, factual verification, audit и rollback. Отдельного быстрого пути для AI не существует.

SMM обязан полностью работать без KAgent. Недоступность KAgent не влияет на мониторинг, Links, provisioning, alerts, backups и аудит.

## Инвариант недоверенного исполнителя

Это главное требование раздела. KAgent Worker — процесс, исполняющий произвольный код по построению: сборка репозитория запускает его build-скрипты. Worker считается недоверенным субъектом, и изолируется он **от control plane**, а не только от внешней сети.

Из инварианта следуют обязательные условия установки Worker на Node:

- отдельный системный пользователь без sudo, отдельные каталоги, отдельный cgroup;
- нет доступа к сокету provisioning-helper `/run/ochenstarik-server-monitor-manager/provisioning.sock`; членство в группе `ochenstarik-smm-agent` запрещено;
- нет доступа к `agent.pfx`, `control-ca.crt`, `agent.env`, `nodes.tsv`, каталогу `mesh` и каталогу rollback;
- нет сетевого доступа к Control API и к порту Control;
- исполнение в контейнере: read-only rootfs, seccomp, AppArmor, сброшенные capabilities, ограничение pids;
- системные лимиты (`CPUQuota`, `MemoryMax`, `TasksMax`, `IOWeight`) дополняют изоляцию, но не заменяют её;
- порт Worker не публикуется наружу.

Компрометация Worker не должна давать ни управления Node, ни доступа к другим Node, ни возможности создать Link.

## Модель возможностей

Три класса, а не два.

**Чтение.** Не меняет состояние.

```text
infrastructure.nodes.read
infrastructure.metrics.read
infrastructure.alerts.read
infrastructure.links.read
infrastructure.jobs.read
infrastructure.drift.read
infrastructure.services.read      (после Горизонта 1)
infrastructure.containers.read    (после Горизонта 1)
infrastructure.logs.read          (после Горизонта 2)
infrastructure.backups.read       (после Горизонта 1)
```

**Запрос.** Создаёт предложение, которое проходит обычный конвейер подтверждения. Само по себе ничего не меняет.

```text
infrastructure.diagnostics.request
infrastructure.link.plan
infrastructure.link.request
infrastructure.provisioning.plan
infrastructure.provisioning.request
infrastructure.backup.request
infrastructure.service.restart.request
infrastructure.remediation.request
```

**Невыдаваемые.** Не «выключены по умолчанию», а отсутствуют как код в пути KAgent-идентичности. Их нельзя включить настройкой.

```text
root.execute
ca.rotate
secrets.read
audit.disable
firewall.apply          (доступен только через .request с режимом operator_reauth)
users.modify            (доступен только через .request)
node.delete
```

Прецедент в проекте: Automation-сертификат физически не может изменять Links — не по умолчанию, а вообще. Здесь применяется та же строгость.

## Идентичность

Отдельная роль `KAgentIntegration`, связанная с installation ID, организацией, списком разрешённых Nodes, набором возможностей, сроком действия, отпечатком сертификата и состоянием отзыва. Выдаётся по одноразовому token, как остальные identity. Не наследует прав Operator и не может выдавать себе новые возможности.

## Локальное обнаружение

```text
/run/server-monitor-manager/integration.json
/run/server-monitor-manager/integration.sock
```

Права `root:smm-integrations`, `0640`, каталог `0750`.

Членства в группе **недостаточно**. Сокет обязан повторить меры, уже реализованные в `ProvisioningHelperServer`:

- проверка `SO_PEERCRED` со сверкой uid обращающегося процесса;
- обработка соединения вне цикла accept, ограничение параллелизма;
- таймаут на соединение целиком;
- чтение буфером с ограничением размера запроса;
- ограничение частоты запросов и отдельный счётчик неавторизованных попыток.

## Протокол

```text
KAgent-SMM Integration Protocol v1
```

Handshake:

```json
{
  "client": "kagent",
  "client_version": "0.8.0",
  "protocol_versions": ["1.0", "1.1"],
  "requested_capabilities": ["nodes.read", "metrics.read", "jobs.plan"]
}
```

Ответ содержит выбранную версию протокола и **фактически выданные** возможности, которые могут быть уже запрошенных:

```json
{
  "server": "server-monitor-manager",
  "server_version": "0.2.0",
  "selected_protocol": "1.1",
  "granted_capabilities": ["nodes.read", "metrics.read"]
}
```

Версия сервера в ответе — реальная версия сборки. Неизвестные поля запроса отклоняются, неизвестные версии протокола не согласуются.

## Поверхность API

Проектируется против того, что существует. Эндпоинты для сущностей, которых в системе нет (containers, services, logs, backups), добавляются вместе с самими сущностями, а не заранее.

Первый этап:

```text
GET  /api/v1/integrations/capabilities
GET  /api/v1/integrations/version
GET  /api/v1/kagent/nodes
GET  /api/v1/kagent/nodes/{id}
GET  /api/v1/kagent/nodes/{id}/metrics
GET  /api/v1/kagent/nodes/{id}/health
GET  /api/v1/kagent/events
```

Второй этап, после появления соответствующих модулей:

```text
POST /api/v1/kagent/links/plan
POST /api/v1/kagent/links/request
GET  /api/v1/kagent/links/{id}
POST /api/v1/kagent/links/{id}/disable-request
POST /api/v1/kagent/jobs/plan
POST /api/v1/kagent/jobs/request
GET  /api/v1/kagent/jobs/{id}
POST /api/v1/kagent/jobs/{id}/cancel-request
POST /api/v1/kagent/diagnostics
GET  /api/v1/kagent/diagnostics/{id}
```

Любая мутация требует idempotency key и audit reason. `*.request` возвращает идентификатор задания, а не результат: результат наступает после подтверждения человеком.

## Временные Links для задач

Обязательные поля: source, destination, протокол, порт, TTL, идентификатор задачи, причина, версия политики, владелец, режим подтверждения.

Ограничения:

- destination не может быть Hub;
- destination не может быть другим Worker;
- TTL обязателен и ограничен сверху; Link без TTL для задачи не создаётся;
- снятие выполняется по завершении задачи, отказу, таймауту, уходу Worker в offline, истечению аренды или аварийной остановке;
- снятие проверяется фактически, как и любая другая Link-операция.

## События

SMM передаёт versioned infrastructure events и не становится внутренней очередью KAgent:

```text
node.online
node.offline
metric.threshold
link.active
link.disabled
provisioning.started
provisioning.completed
provisioning.failed
certificate.expiring
backup.failed
worker.resource_exceeded
worker.quarantined
```

## Аварийные средства

Operator может в любой момент, без участия KAgent: отозвать сертификат интеграции, остановить все Worker, отключить задачные Links, заблокировать новые запросы, перевести интеграцию в режим только чтения, прекратить аренды, поместить Worker в карантин, отключить его сеть. Журналы для разбора сохраняются.

## Поведение при отказе

Недоступность KAgent не влияет на работу SMM. Задание с неопределённым результатом получает `NeedsReconciliation`; после восстановления связи проверяется фактическое состояние, а не предполагается успех.

## Вне области

- root shell в любой форме;
- выдача приватных ключей и содержимого секретов;
- изменение или отключение аудита;
- произвольные правила firewall;
- прямое управление пользователями без конвейера подтверждения;
- превращение SMM в очередь задач или планировщик KAgent.
