# Antigravity: завершить подписанную поставку, Queue B

## Исходное состояние

- Репозиторий: `ochenstarik-ui/server-monitor-manager`.
- Ветка: `antigravity/signed-delivery-queue-b`.
- Актуальная база на момент задания: `main` / `v0.1.0-alpha.9` @ `2ca059f`.
- Коммит `ef137bc` уже добавляет bootstrap-проверку manifest и совместимости компонентов.
- Незакоммичены `MainPage.xaml`, `MainPage.xaml.cs` и `tests/bootstrap/test-manifest-verification.sh`.
- PR и CI для Queue B отсутствуют.
- PR #34 с repo hygiene открыт отдельно. Не включать его изменения вручную. Перед публикацией ветки снова получить актуальный `main` и перебазироваться, если #34 или другой PR был смержен.

## Цель

Довести проверку подписанных обновлений Desktop и bootstrap до fail-closed реализации, покрытой изолированными тестами, и открыть готовый к независимой приёмке PR. Релиз и тег не создавать — это отдельный gate Hermes.

## Обязательная работа

### 1. Сохранить и довести bootstrap

- `verify_archive` получает ожидаемый SHA-256 из проверенного manifest, а не из соседнего `.sha256`.
- `verify-manifest MANIFEST SIGNATURE` проверяет подпись production keyless identity.
- Issuer и identity-regexp зафиксированы в коде; рядом с identity должна быть ссылка на `docs/release-policy.md`.
- Отсутствующий `cosign`, manifest или signature, чужая identity, неверная подпись и неверный hash завершаются отказом.
- `SMM_ALLOW_UNSIGNED=1` остаётся только явным аварийным developer bypass с громким предупреждением; default — fail closed.
- `update-control` и `update-agent` блокируют несовместимую пару Control ↔ Agent ↔ helper по manifest v2.
- Проверить CLI-контракт: тесты должны передавать ровно те аргументы, которые принимает команда. Текущий bootstrap-тест ошибочно передаёт `verify-manifest` три аргумента при контракте из двух.

### 2. Переделать Desktop updater для тестируемости

- Не использовать закрыто созданный внутри класса `HttpClient` и реальный GitHub в unit tests. Внедрить HTTP transport, signature verifier, файловое хранилище и launcher либо эквивалентные тестовые границы.
- Поддержать alpha/pre-release update channel явно. `releases/latest` нельзя считать источником текущего alpha-релиза без отдельного контракта и теста.
- Manifest, signature и MSIX должны принадлежать одному release/tag; нельзя смешивать URL или ассеты разных тегов.
- Проверить manifest keyless-подпись pinned issuer/identity до показа кнопки обновления.
- После загрузки MSIX сверить его SHA-256 с уже проверенным manifest и только затем разрешить запуск.
- Отсутствующая, чужая или неверная подпись и несовпадающий hash — безусловный отказ. В production нет кнопки «продолжить всё равно».
- Отказ записывается в штатную диагностику приложения; одного `Debug.WriteLine` недостаточно.
- Developer bypass, если он сохраняется для Desktop, должен быть явным, выключенным по умолчанию и покрытым тестом; он не должен попадать в обычный пользовательский путь.
- Не скачивать доверенный ключ/identity из проверяемого релиза.
- Не запускать установщик автоматически. Пользователь видит действие только после успешной проверки metadata.

### 3. Исправить тесты

Обязательны полностью offline и детерминированные проверки:

1. корректная production-equivalent подпись и hash принимаются;
2. manifest, подписанный другой identity, отклоняется;
3. подписанный manifest с hash, не совпадающим с MSIX/архивом, отклоняется;
4. отсутствующая signature отклоняется;
5. повреждённая signature отклоняется;
6. отсутствующий verifier отклоняется;
7. несовместимые версии Control/Agent/helper блокируют оба `update-*`;
8. Desktop не показывает update action до завершения всех проверок;
9. prerelease-channel корректно находит `alpha.9/alpha.10`, а не полагается на stable-only endpoint;
10. URL/asset из другого тега отклоняется.

`UpdateServiceTests.cs` должен реально компилировать production-код. Сейчас тестовый `.csproj` не подключает `UpdateService.cs`, а существующие четыре теста проверяют только создание объекта, cancellation, `NullReferenceException` и свойства record — это не критерии Queue B.

Bootstrap-тест не должен объявлять успех, если production implementation игнорирует его тестовый ключ. Тестовый seam допустим только если он недоступен в обычном production path и не ослабляет pinned identity.

### 4. Проверить фактический alpha.9

- Скачать manifest и signature именно из неизменяемого `v0.1.0-alpha.9`.
- Подтвердить, что выбранная команда `cosign verify-blob` действительно проверяет опубликованный формат `.sig` и pinned identity.
- Проверить hash хотя бы одного Linux archive и MSIX против manifest.
- Если опубликованной `.sig` недостаточно для offline/keyless verification и требуется certificate/bundle, не ослаблять проверку и не менять workflow самостоятельно. Зафиксировать точный producer-side gap в отчёте для Hermes.

### 5. Git и CI

- Не включать `ci_log*.txt`, ключи, временные manifest/signature, скачанные binaries и тестовые архивы.
- Не менять `.github/workflows/**`. Если producer-side изменение действительно необходимо, описать его для Hermes.
- Сначала синхронизировать ветку с текущим `main`, затем оформить логические коммиты.
- Прогнать `actionlint` на существующих workflow, shellcheck/bash syntax для изменённых shell-файлов, Desktop tests, Control tests и релевантные acceptance/bootstrap tests.
- Открыть PR, дождаться Linux и Windows CI. Не merge.
- Не запускать публикацию на существующем теге и не создавать `alpha.10`.

## Критерий приёмки

- Реальные четыре security-сценария Desktop и расширенный negative suite зелёные.
- Bootstrap и Desktop используют один зафиксированный trust policy и отказывают при любой неоднозначности.
- Проверка опубликованных ассетов `alpha.9` воспроизводима и приложена к отчёту.
- Версионная несовместимость блокируется до изменения установленной системы.
- Рабочее дерево чистое, ветка основана на текущем `main`, лишних файлов нет.
- PR открыт, все обязательные CI зелёные, workflow не изменены, PR не смержен.

## Отчёт

Раздельно перечислить:

- коммиты и изменённые файлы;
- локальные команды и результаты;
- GitHub CI со ссылками;
- проверку фактических `alpha.9` assets;
- negative tests;
- не запускавшиеся проверки и причину;
- producer-side требования для Hermes, если обнаружены;
- ссылку на PR.

