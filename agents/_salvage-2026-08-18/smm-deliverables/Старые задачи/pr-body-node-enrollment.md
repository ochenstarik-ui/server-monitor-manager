## Antigravity: выдача кода регистрации узла из приложения — Этап 1

Реализован HTTP-эндпоинт для выпуска кода регистрации узла (`SMMNODE2...`) из Control в соответствии с требованиями `smm-antigravity-task-web-console-2026-08-17.md`.

### Что сделано
1. **Эндпоинт Control:**
   - Добавлен единственный канонический маршрут `POST /api/v1/control/agents/{nodeId}/enrollment-code` (дублирующий маршрут `/nodes/...` удален в соответствии с требованиями доработки).
   - Доступ строго ограничен ролью `Operator` (политика авторизации `Operator` на группе `/api/v1/control`). Роли `Agent` и `Automation`, а также неаутентифицированные запросы отклоняются (401/403).
   - Подключена политика ограничения частоты `"enrollment"`.
2. **Формат кода `SMMNODE2`:**
   - Выдаваемый код имеет 9 сегментов, разделенных точками, и собирается побайтно идентично эталонной bash-функции `create_node_code`:
     `SMMNODE2.<control_url>.<ca_pem>.<node_id>.<token>.<hub_endpoint>.<hub_public_key>.<node_address>.<mesh_network>`
   - Каждый сегмент кодируется в base64url без выравнивания (`=`).
3. **Резервирование адресов Mesh (`10.77.0.0/24`):**
   - Выделение адресов реализовано в сервисе `NodeEnrollmentService` в полном соответствии с поведением `reserve_node_address`:
     - Адреса последовательно выделяются из пула `10.77.0.2` .. `10.77.0.254` и фиксируются в `nodes.tsv`.
     - При повторном запросе для уже существующего `node_id` возвращается ранее выделенный IP-адрес, и для него генерируется новый одноразовый 10-минутный токен.
     - Два разных `node_id` гарантированно получают уникальные непересекающиеся адреса.
4. **Валидация и безопасность:**
   - Валидация имени узла выполняется через `NodeIdValidator.IsValid(nodeId)` до создания токена и до выделения адреса.
   - Недопустимые имена (пустые, заглавные буквы, подчеркивания, спецсимволы, длина > 63 символов) отклоняются с 400 Bad Request.
   - Имя узла не подставляется в системные команды оболочки.
5. **Журналирование и аудит:**
   - Выдача кода фиксируется в таблице аудита `audit` с действием `agent.enrollment_code.issued` (актор, `node_id`, выделенный адрес, дата истечения).
   - Событие публикуется в `ControlEventBroker` для real-time подписчиков.

---

### Отчёт о тестировании

#### 1. Локальные тесты (PASS)
- Сборка решения .NET 10 SDK: `dotnet build` — 0 ошибок.
- Набор модульных и интеграционных тестов: `dotnet test` — **134 пройденных теста** (включая 8 новых тестов в `NodeEnrollmentCodeTests.cs`):
  - `AnonymousRequestIsRejectedWithUnauthorized` — 401 Unauthorized;
  - `AgentRoleIsRejectedWithForbidden` — 403 Forbidden;
  - `AutomationRoleIsRejectedWithForbidden` — 403 Forbidden;
  - `InvalidNodeIdIsRejectedWithBadRequestBeforeTokenCreation` — 400 Bad Request, токены в БД не создаются;
  - `EmptyNodeIdThrowsArgumentExceptionInService` — отказ на уровне сервиса;
  - `OperatorCanRequestNodeEnrollmentCodeWithCorrectStructure` — 200 OK, 9 сегментов `SMMNODE2`, валидация содержимого каждого сегмента;
  - `TwoDifferentNodeIdsGetDistinctMeshAddresses` — разным узлам выдаются разные адреса;
  - `RepeatedRequestForSameNodeIdReusesReservedAddress` — повторный запрос возвращает тот же IP с новым токеном;
  - `AuditLogRecordsEnrollmentCodeIssuance` — проверка записи в таблицу `audit`;
  - `CodeStructureMatchesBashReferenceFormatFixture` — посимвольная проверка структуры алфавита base64url против эталона.

#### 2. CI Workflows (PASS, все зелёные)
- **Linux control and agent (PR #53):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030829
  - Job `build-and-test`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030829/job/95450174266
- **Linux platform matrix (PR #53):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797
  - Jobs:
    - Release archive (linux-x64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450173473
    - Release archive (linux-arm64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450173289
    - Ubuntu 22.04 x64 / arm64 native VM: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502986 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502748
    - Ubuntu 24.04 x64 / arm64 native VM: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502601 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502629
    - Debian 12 x64 / arm64 systemd restart: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502671 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502617
    - Debian 13 x64 / arm64 systemd restart: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450502675 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030797/job/95450504082
- **Windows build (PR #53):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030871
  - Job `build`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32051030871/job/95450173256

#### 3. Что не проверялось и почему
- Реальный запуск физического установщика bash на внешнем сервере через этот HTTP-эндпоинт не производился, так как веб-интерфейс и интеграция с CLI-клиентом запланированы на Этапе 2 после слияния Этапа 1 в `main`. Структурная и побайтовая эквивалентность выданного кода `SMMNODE2` подтверждена тестом `CodeStructureMatchesBashReferenceFormatFixture`.
