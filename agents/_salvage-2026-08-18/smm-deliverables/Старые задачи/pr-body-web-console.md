## Antigravity: веб-консоль оператора — Этап 2

Реализована веб-консоль оператора для управления узлами, сетевыми связями (Links) и выдачи кодов регистрации (`smm-antigravity-task-web-console-2026-08-17.md`).

### База
- Базовая ветка: `main` @ `f7b10ac`
- Ветка: `antigravity/web-console`

---

### Что сделано
1. **Веб-интерфейс консоли оператора (SPA):**
   - **Список узлов:** загрузка реальных данных из `GET /api/v1/control/agents` (имя узла, статус, версия агента, последний heartbeat, состояние сертификата).
   - **Список сетевых связей (Links):** загрузка реальных данных из `GET /api/v1/control/links` (ID связи, источник, назначение, протокол/порт, требуемое и фактическое состояние, срок действия).
   - **Кнопка «Добавить узел» и модальное окно:**
     - Валидация имени узла (`1-63` символа, строчные буквы, цифры, дефисы).
     - Вызов эндпоинта `POST /api/v1/control/agents/{nodeId}/enrollment-code`.
     - Крупное отображение выданного кода `SMMNODE2...` и отпечатка CA (SHA-256) с копированием в буфер обмена в один клик.
     - **Обязательное заметное предупреждение:** «При выполнении установщика на сервере обязательно сверьте отпечаток CA, выведенный скриптом, с отпечатком ниже. Подтверждение без сверки обесценивает криптографическую проверку подписи!».
     - 10-минутный обратный отсчет срока действия кода.
2. **Интеграция в Control backend и поддержка встроенных ресурсов (Single-File Publish):**
   - Веб-ресурсы размещены в `src/ServerMonitorManager.Control/wwwroot/` (`index.html`, `style.css`, `app.js`) и встроены как `EmbeddedResource` в сборку Control, сохраняя единую разворачиваемую единицу (`ochenstarik-smm-control`) при `PublishSingleFile=true`.
   - Маршруты консоли (`/`, `/index.html`, `/style.css`, `/app.js`, `/console`) защищены авторизацией роли `Operator` (`.RequireAuthorization("Operator")`).
   - Механизм `GetWebConsoleAsset` проверяет наличие файлов на диске и автоматически переключается на извлечение из `EmbeddedResource` при отсутствии каталога `wwwroot` на диске.
3. **Безопасность доступа (mTLS):**
   - Доступ к статическим ресурсам и API консоли строго ограничен ролью `Operator`.
   - Неаутентифицированные запросы отклоняются с `401 Unauthorized`.
   - Роли `Agent` и `Automation` получают отказ с `403 Forbidden`.

---

### Отчёт о тестировании

#### 1. Локальные тесты (PASS)
- Сборка решения: `dotnet build` — 0 ошибок.
- Набор тестов: `dotnet test` — **150 пройденных тестов** (включая 16 интеграционных проверок в `WebConsoleTests.cs`):
  - `AnonymousAccessToWebConsoleRoutesIsRejectedWithUnauthorized` — 401 Unauthorized на маршрутах `/`, `/index.html`, `/style.css`, `/app.js`, `/console`;
  - `AgentRoleIsForbiddenFromWebConsole` — 403 Forbidden для роли `Agent`;
  - `AutomationRoleIsForbiddenFromWebConsole` — 403 Forbidden для роли `Automation`;
  - `OperatorCanAccessWebConsoleHtmlAndAssets` — 200 OK с корректными MIME-типами (`text/html`, `text/css`, `application/javascript`);
  - `WebConsoleHtmlContainsRequiredUiElementsAndWarning` — проверка наличия элементов интерфейса (таблицы, форма добавления узла, поля вывода кода, отпечатка CA, кнопки копирования и текста предупреждения о сверке отпечатка);
  - `EmbeddedAssetsAreServedWhenDiskWebRootIsMissing` — **тест ветки встроенных ресурсов**: поднятие тестового хоста с пустым/отсутствующим `WebRootPath` подтверждает, что маршруты `/`, `/index.html`, `/style.css`, `/app.js` и `/console` успешно возвращают 200 OK, ожидаемые MIME-типы и непустое содержимое из ресурсов сборки `EmbeddedResource`.

#### 2. CI Workflows (PASS, все зелёные)
- **Linux control and agent (PR #59):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203480
  - Job `build-and-test`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203480/job/95482658110 — **PASS** (4m 34s)
- **Linux platform matrix (PR #59):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491
  - Jobs:
    - Release archive (linux-x64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95482658550 — **PASS**
    - Release archive (linux-arm64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95482658482 — **PASS**
    - Ubuntu 22.04 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027945 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027753 — **PASS**
    - Ubuntu 24.04 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027806 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027824 — **PASS**
    - Debian 12 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027885 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027803 — **PASS**
    - Debian 13 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027739 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203491/job/95483027857 — **PASS**
- **Windows build (PR #59):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203452
  - Job `build`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32061203452/job/95482657920 — **PASS** (3m 21s)

#### 3. Границы изменений
- `git diff --name-only origin/main`:
  - `src/ServerMonitorManager.Control/Program.cs`
  - `src/ServerMonitorManager.Control/ServerMonitorManager.Control.csproj`
  - `src/ServerMonitorManager.Control/wwwroot/app.js`
  - `src/ServerMonitorManager.Control/wwwroot/index.html`
  - `src/ServerMonitorManager.Control/wwwroot/style.css`
  - `tests/ServerMonitorManager.Control.Tests/WebConsoleTests.cs`
- Файлы `deploy/**`, релизные workflow и защищённые каталоги не затрагивались.

#### 4. Что не проверялось и почему
- Вход по логину и паролю без mTLS не реализовывался, так как является предметом изолированного Этапа 3 и должен разрабатываться после слияния Этапа 2 в `main`.
