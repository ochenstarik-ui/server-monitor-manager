## Antigravity: вход в консоль по логину и паролю для тестов — Этап 3

Реализован механизм входа в консоль по логину и паролю для тестовых сред в соответствии с `smm-antigravity-task-password-login-2026-08-18.md`.

### База
- Базовая ветка: `main` @ `25bfa8d`
- Ветка: `antigravity/password-login`

---

### Что сделано

1. **Конфигурация и отключение по умолчанию:**
   - Добавлена секция `Authentication:PasswordLogin` (`PasswordLoginOptions`):
     - `EnabledForTesting` (по умолчанию `false`);
     - `Username` (имя пользователя для входа);
     - `PasswordHash` (подбор-стойкий хеш пароля);
     - `SessionTtlMinutes` (время жизни сессии, по умолчанию 60 мин).
   - Если флаг выключен (`false`), эндпоинт `/api/v1/auth/login` возвращает `404 Not Found`, а консоль и API остаются строго закрыты требованием mTLS-сертификата роли `Operator`.

2. **Предупреждение в логе при каждом старте:**
   - При старте Control с `EnabledForTesting=true` в лог выводится обязательное предупреждение уровня `Warning`:
     `SECURITY WARNING: Password login is enabled for testing purposes (Authentication:PasswordLogin:EnabledForTesting=true). Do not enable in production environments!`.

3. **Подбор-стойкое хеширование и защита от атак по времени (Timing Attacks):**
   - Реализован `PasswordHasher` на базе `Rfc2898DeriveBytes.Pbkdf2` с 600,000 итераций SHA-256, 16-байтным криптографически стойким salt и сравнением в константном времени (`CryptographicOperations.FixedTimeEquals`).
   - Для неизвестных пользователей и неверных паролей выполняется холостой расчет PBKDF2 (`PerformDummyVerification`) и искусственная задержка (500 мс), гарантирующие одинаковое время ответа сервера независимо от существования пользователя.

4. **Ограничение частоты попыток (Rate Limiting):**
   - На эндпоинт `/api/v1/auth/login` назначена политика ограничения частоты `"password-login"` (лимит 5 запросов в минуту на IP). При превышении возвращается `429 Too Many Requests`.

5. **Ограниченная по времени сессия и эндпоинты аутентификации:**
   - `PasswordSessionService` генерирует криптографически стойкий 32-байтный сессионный токен с ограниченным сроком действия (`SessionTtlMinutes`).
   - `POST /api/v1/auth/login` возвращает токен и устанавливает `HttpOnly`, `SameSite=Strict`, `Secure` cookie `smm_session`.
   - `POST /api/v1/auth/logout` отзывает токен и очищает cookie.
   - `GET /api/v1/auth/status` возвращает статус `enabledForTesting`.

6. **Ограничение роли (только Operator) и сохранение строгого приоритета mTLS:**
   - Сессионный токен предоставляет исключительно роль `Operator`. Попытки доступа к маршрутам `Automation` (`/api/v1/automation/**`) или `Agent` (`/api/v1/agents/**`) отклоняются с кодом `403 Forbidden`.
   - В схеме авторизации `Combined` наличие клиентского сертификата имеет безусловный приоритет перед токеном/cookie.

7. **Веб-интерфейс консоли:**
   - Добавлена форма входа в консоль оператора при включенном режиме тестирования.
   - Добавлен предупреждающий баннер «⚠️ Режим тестирования: включен вход по логину и паролю».
   - Добавлена кнопка «Выйти» для завершения сессии и отзыва токена.

---

### Отчёт о тестировании

#### 1. Локальные тесты (PASS)
- Сборка решения: `dotnet build` — 0 ошибок.
- Набор тестов: `dotnet test` — **157 пройденных тестов** (включая 7 новых интеграционных проверок в `PasswordLoginTests.cs`):
  - `WhenPasswordLoginIsDisabledEndpointsReturnDisabledStatusAndRejectLogin` — при `EnabledForTesting=false` эндпоинт входа отвечает 404, а доступ к Control без сертификата отклонён (401);
  - `WhenPasswordLoginIsEnabledSuccessfulLoginGrantsOperatorRoleOnly` — успешный вход выпускает токен и cookie, открывает доступ к `GET /api/v1/control/agents`, `GET /api/v1/control/links` и `POST /api/v1/control/agents/{id}/enrollment-code`;
  - `PasswordSessionTokenCannotAccessAutomationRoutes` — токен оператора отклоняется с `403 Forbidden` на маршрутах `/api/v1/automation/links` и `/api/v1/agents/provisioning/jobs/next`;
  - `WrongPasswordAndUnknownUserBothFailWithUnauthorizedAndUniformExecution` — неверный пароль и неизвестный пользователь одинаково возвращают 401;
  - `ExceedingRateLimitRejectsWithTooManyRequests` — превышение 5 попыток входа в минуту возвращает `429 Too Many Requests`;
  - `ClientCertificateAuthenticationHasPriorityAndWorksUnderBothModes` — аутентификация по клиентскому сертификату mTLS работает и имеет приоритет как при включенном, так и при выключенном флаге парольного входа;
  - `LogoutRevokesSessionToken` — вызов `/api/v1/auth/logout` немедленно отзывает сессионный токен, повторные запросы отклоняются с 401.

#### 2. CI Workflows (PASS, все зелёные)
- **Linux control and agent (PR #61):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357096
  - Job `build-and-test`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357096/job/95489511205 — **PASS** (6m 46s)
- **Linux platform matrix (PR #61):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022
  - Jobs:
    - Release archive (linux-x64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489511310 — **PASS**
    - Release archive (linux-arm64): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489511207 — **PASS**
    - Ubuntu 22.04 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904683 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904802 — **PASS**
    - Ubuntu 24.04 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904730 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904701 — **PASS**
    - Debian 12 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904745 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904821 — **PASS**
    - Debian 13 x64 / arm64: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904733 / https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357022/job/95489904746 — **PASS**
- **Windows build (PR #61):**
  - Run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357105
  - Job `build`: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32063357105/job/95489510886 — **PASS** (3m 43s)

#### 3. Границы изменений
- `git diff --name-only origin/main`:
  - `src/ServerMonitorManager.Control/PasswordHasher.cs`
  - `src/ServerMonitorManager.Control/PasswordLoginOptions.cs`
  - `src/ServerMonitorManager.Control/PasswordSessionAuthenticationHandler.cs`
  - `src/ServerMonitorManager.Control/PasswordSessionService.cs`
  - `src/ServerMonitorManager.Control/Program.cs`
  - `src/ServerMonitorManager.Control/wwwroot/app.js`
  - `src/ServerMonitorManager.Control/wwwroot/index.html`
  - `src/ServerMonitorManager.Control/wwwroot/style.css`
  - `src/ServerMonitorManager.Core/PasswordLoginModels.cs`
  - `src/ServerMonitorManager.Core/SmmJsonContext.cs`
  - `tests/ServerMonitorManager.Control.Tests/PasswordLoginTests.cs`
- Файлы `deploy/**`, релизные workflow и защищённые каталоги не затрагивались.
