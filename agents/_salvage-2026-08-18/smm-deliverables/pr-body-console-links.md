## Antigravity: управление Links и журнал событий в веб-консоли

Реализовано создание и аварийное отключение сетевых связей (Links), раздельное отображение желаемого и фактического состояний, явное подтверждение направления сетевого доступа, обязательное обоснование (`Reason`), клиентская генерация `IdempotencyKey`, отображение сроков действия, а также раздел «Журнал событий» на базе потока `GET /api/v1/control/events` в соответствии с `smm-antigravity-task-console-links-2026-08-18.md`.

### База
- Базовая ветка: `main` @ `7c43977`
- Ветка: `antigravity/console-links`

---

### Что сделано

1. **Раздельное отображение желаемого (`DesiredState`) и фактического (`ActualState`) состояний:**
   - В таблице сетевых связей добавлены раздельные колонки «Желаемое» и «Фактическое».
   - Добавлен визуальный индикатор рассогласования (`state-mismatch-badge`: «⚠️ Не синхронизировано»), когда `desiredState !== actualState`.
   - Добавлено отображение `LastError` при его наличии в виде выделенного предупреждающего блока в ячейке состояния.

2. **Создание Link с явным подтверждением направления:**
   - Добавлена кнопка «Создать Link» и модальное окно `#create-link-modal` с формой ввода:
     - `Source Node ID` (выбор из списка зарегистрированных узлов или ручной ввод);
     - `Target Node ID` (выбор из списка зарегистрированных узлов или ручной ввод);
     - `Protocol` (`TCP` / `UDP`);
     - `Port` (1–65535);
     - `TTL` (срок действия в минутах);
     - `Reason` (обязательное поле причины).
   - Реализован динамический блок подтверждения направления правила доступа `#link-direction-summary`, наглядно показывающий оператору одной строкой:
     `Открытие доступа: с узла «<source>» к узлу «<target>», протокол <protocol>, порт <port>, на <ttl> мин.`.
   - Добавлена валидация различия источника и назначения (`source !== target`).

3. **Обязательное осмысленное поле `Reason`:**
   - Проверяется заполненность причины на клиенте (непустая строка без пробелов до 256 символов) с понятным сообщением об ошибке оператору.

4. **Аварийное быстрое отключение Link:**
   - В каждой строке таблицы Link добавлена заметная кнопка «Отключить» (`.btn-danger`), доступная без перехода на другие страницы.
   - Отключение подтверждается в один шаг и отправляет `POST /api/v1/control/links/{id}/disable`.
   - Если Link уже отключён, строка показывает нейтральный статус «Отключено».

5. **Клиентская генерация `IdempotencyKey`:**
   - Ключ идемпотентности генерируется на клиенте с помощью `crypto.randomUUID()` при открытии формы создания или нажатии на отключение.
   - При повторной отправке той же формы (или при сбое сети) ключ сохраняется, предотвращая дублирование правил.

6. **Отображение срока жизни (Expiration):**
   - Для каждого Link рассчитывается и выводится время до истечения (например, `через 45 мин. (14:30)`) либо отметка «Истёк» при завершении срока действия.

7. **Журнал событий (Event Journal):**
   - Добавлен раздел «Журнал событий» (`#events-section`) с таблицей `#events-table` и счётчиком событий.
   - Консоль в реальном времени подключается к `GET /api/v1/control/events` (NDJSON-поток) и выводит поступающие события (время, тип события, субъект/узел, подробности payload).
   - При получении событий изменения топологии (`link.*`, `agent.*`) автоматически инициируется фоновое обновление данных дашборда.

8. **Текстовое отображение ошибок сервера:**
   - Ошибки валидации или конфликтов сервера парсятся из Problem Details (`detail`, `title`, `errors`) и выводятся понятным текстом в модальном окне или глобальном баннере.

9. **Сохранение существующих эндпоинтов и архитектуры:**
   - Новых эндпоинтов в Control не добавлялось; используются исключительно существующие API (`GET/POST /api/v1/control/links`, `POST /api/v1/control/links/{id}/disable`, `GET /api/v1/control/events`).

---

### Отчёт о тестировании

#### 1. Локальные тесты (PASS)
- Сборка: `dotnet build` — 0 ошибок.
- Набор тестов: `dotnet test` — **158 пройденных тестов** (включая обновленный `WebConsoleHtmlContainsRequiredUiElementsAndWarning` и новый `WebConsoleProvidesLinkManagementAndEventsJournal`):
  - `WebConsoleHtmlContainsRequiredUiElementsAndWarning` — проверка наличия всех элементов управления Link (кнопка создания, модальное окно, поля формы, подсказка направления, таблицы и счётчики);
  - `WebConsoleProvidesLinkManagementAndEventsJournal` — проверка раздельных колонок Desired/Actual/LastError, карточки подтверждения направления и раздела журнала событий;
  - `EmbeddedAssetsAreServedWhenDiskWebRootIsMissing` — проверка отдачи обновленных встроенных HTML/CSS/JS ресурсов сборки;
  - `AnonymousAccessToWebConsoleRoutesIsRejectedWithUnauthorized` — анонимный доступ отклонён;
  - `AgentRoleIsForbiddenFromWebConsole` и `AutomationRoleIsForbiddenFromWebConsole` — роли Agent и Automation запрещены.

#### 2. CI Workflows (PASS, 100% зелёные)
- **Linux control and agent (PR #63):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077084
  - Job `build-and-test`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077084/job/95604415288 — **PASS** (5m 54s)
- **Linux platform matrix (PR #63):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060
  - Jobs:
    - Release archive (linux-x64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604415185 — **PASS**
    - Release archive (linux-arm64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604415135 — **PASS**
    - Ubuntu 22.04 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702441 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702493 — **PASS**
    - Ubuntu 24.04 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702484 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702443 — **PASS**
    - Debian 12 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702437 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702444 — **PASS**
    - Debian 13 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702418 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077060/job/95604702394 — **PASS**
- **Windows build (PR #63):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077075
  - Job `build`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32102077075/job/95604415017 — **PASS** (4m 18s)

#### 3. Границы изменений
- `git diff --name-only origin/main`:
  - `src/ServerMonitorManager.Control/wwwroot/app.js`
  - `src/ServerMonitorManager.Control/wwwroot/index.html`
  - `src/ServerMonitorManager.Control/wwwroot/style.css`
  - `tests/ServerMonitorManager.Control.Tests/WebConsoleTests.cs`
- Файлы `deploy/**`, релизные workflow и bootstrap-тесты не затрагивались.
