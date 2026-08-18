## Antigravity: доказать проверку подписи на стороне Desktop

Реализован и проверен набор тестов приёмки и негативных проверок против опубликованного релиза (`smm-antigravity-task-desktop-update-proof-2026-08-13.md`).

---

### Что сделано
1. **Приёмочный тест против опубликованного релиза (`AcceptanceRealReleaseManifestAndSignatureAreAccepted`):**
   - Скачивает `server-monitor-manager-manifest.json`, `.sig` и `.pem` для тега релиза через публичный HTTP-транспорт без `gh` и без токенов.
   - Тег релиза параметризован через переменную окружения `SMM_TEST_RELEASE_TAG` со значением по умолчанию `v0.1.0-alpha.14` и поддержкой проверки свежих релизов (включая `v0.1.0-alpha.18`).
   - Прогоняет настоящие файлы релиза через `ProcessSignatureVerifier` и `UpdateService`.
   - Подтверждает, что подпись и сертификат успешно верифицируются через `cosign` (Fulcio OIDC identity `linux-release.yml`), извлекается корректная версия, валидный URL скачивания MSIX и хэш SHA-256.
2. **Четыре негативных сценария на реальном материале:**
   - `Negative1TamperedHashInRealManifestIsRejected`: динамическая подмена хэша в настоящем manifest приводит к отказу верификации подписи;
   - `Negative2RealManifestWithSignatureFromDifferentIdentityIsRejected`: подпись от посторонней identity отвергается;
   - `Negative3MissingCertificateIsRejected`: отсутствие сертификата приводит к отказу;
   - `Negative4CertificateFromDifferentWorkflowIsRejected`: сертификат от другого workflow/эмитента отвергается.
3. **Разделение тестов на два независимых шага в `windows-build.yml`:**
   - **`Test Desktop security`**: выполняет `dotnet test ... --filter Category!=LiveRelease` — основной быстрый офлайн-гейт без обращения к внешним сетевым ресурсам;
   - **`Test Desktop security (live release)`**: выполняет `dotnet test ... --filter Category=LiveRelease` с `SMM_TEST_RELEASE_TAG: v0.1.0-alpha.18` — изолированный сетевой шаг проверки реального релиза.
4. **Подтверждение исполнения тестов в Windows CI:**
   - Факт реального исполнения доказан намеренной поломкой (несовпадение assertion по версии) с падением шага `Test Desktop security` в Windows CI (прогон 32049692162).

---

### Отчёт о тестировании

#### 1. Локальное тестирование (PASS)
- Полный прогон: `dotnet test tests/ServerMonitorManager.Desktop.Security.Tests` — **29 тестов пройдено** (24 модульных + 5 тестов реального релиза).
- Офлайн-прогон: `dotnet test --filter Category!=LiveRelease` — **24 теста пройдено** (266 мс).

#### 2. Доказательство исполнения в Windows CI через намеренную поломку (FAIL -> PASS)
- **Красный прогон (намеренная поломка):**
  - Workflow run: https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32049692162
  - Job `build` (step `Test Desktop security`): https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32049692162/job/95445812073
  - Ошибка в логе CI:
    ```
    [xUnit.net 00:00:35.62]     ServerMonitorManager.Desktop.Security.Tests.LiveReleaseUpdateVerificationTests.Acceptance_RealReleaseManifestAndSignatureAreAccepted [FAIL]
      Failed ServerMonitorManager.Desktop.Security.Tests.LiveReleaseUpdateVerificationTests.Acceptance_RealReleaseManifestAndSignatureAreAccepted [7 s]
      Error Message:
       Assert.Equal() Failure: Strings differ
                ↓ (pos 1)
       Expected: "v999.999.999-intentional-failure"
       Actual:   "v0.1.0-alpha.14"
    Failed!  - Failed: 1, Passed: 28, Skipped: 0, Total: 29, Duration: 34 s
    ```
- **Зелёные прогоны (с раздельными шагами в `windows-build.yml`):**
  - **Windows build (PR #55):** [Run 32053732808](https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32053732808) / [Job 95458998098](https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32053732808/job/95458998098) — **PASS**
    - `Test Desktop security` (offline) — PASS
    - `Test Desktop security (live release)` (alpha.18) — PASS
  - **Linux control and agent (PR #55):** [Run 32053732805](https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32053732805) / [Job 95458997999](https://github.com/ochenstarik-ui/server-monitor-manager/actions/runs/32053732805/job/95458997999) — **PASS**

#### 3. Границы изменений
- `git diff --name-only origin/main`:
  - `.github/workflows/windows-build.yml` (разделение шагов запуска тестов)
  - `tests/ServerMonitorManager.Desktop.Security.Tests/LiveReleaseUpdateVerificationTests.cs` (тесты проверки реального релиза)
- Все изменения находятся строго в разрешённой области.

#### 4. Что не запускалось и почему
- Физическая установка скачанного MSIX пакета через запуск UI установщика Windows не выполнялась в автоматическом тесте, так как цель задания — строгая криптографическая проверка подписи и хэша manifest/MSIX на реальных артефактах релиза в тестовом окружении.
