# Hermes: независимая приёмка Queue B, merge и релиз alpha.10

## Когда начинать

Начать review после того, как Antigravity откроет PR из `antigravity/signed-delivery-queue-b` и сообщит о зелёном CI. До этого не редактировать его рабочую ветку и не выпускать новый релиз.

Baseline на момент постановки:

- `main` и `v0.1.0-alpha.9`: `2ca059f`;
- PR #32 и #33 смержены;
- Release pipeline `alpha.9` зелёный;
- PR #34 repo hygiene открыт отдельно;
- Queue B PR пока отсутствует.

## Часть 1 — независимый review Queue B

### Проверить Git и scope

- Получить свежие refs и убедиться, что ветка основана на актуальном `main`. Если до review смержен PR #34 или другой PR, потребовать rebase до приёмки.
- Убедиться, что `.github/workflows/**` не изменены Antigravity, нет `ci_log*.txt`, test keys, скачанных binaries и временных release-файлов.
- Проверить историю: bootstrap-файл менялся только после merge PR #32/#33; правила single-writer и immutable release сохранены.

### Проверить Desktop

- Trust identity/issuer вшиты в production-код и соответствуют `linux-release.yml` и `docs/release-policy.md`.
- Manifest и signature проверяются до показа update action.
- MSIX hash проверяется после загрузки и до запуска.
- Все assets связаны с одним tag/release.
- Alpha/pre-release channel работает явно и тестируемо; stable-only `releases/latest` не используется как недоказанное решение.
- Отказ fail closed и попадает в штатную диагностику.
- Нет пользовательского обхода подписи.
- Unit tests не выполняют скрытых сетевых вызовов и действительно компилируют production updater.

### Проверить bootstrap

- `verify-manifest` и тесты имеют одинаковую сигнатуру CLI.
- Production keyless verification соответствует формату signature, реально опубликованному в `alpha.9`.
- Положительный тест использует эквивалентный production trust flow; тестовый ключ не подменяет production identity незаметно.
- Manifest hash, missing signature/tool, foreign identity, tampering и version mismatch покрыты отрицательными тестами.
- `SMM_ALLOW_UNSIGNED=1` остаётся явным developer-only bypass; default path fail closed.

### Независимые проверки

- Повторить проверку manifest/signature и hashes на неизменяемых assets `v0.1.0-alpha.9`.
- Сделать mutation proof: временно поменять expected identity или один manifest hash и показать падение точного теста; временную правку не коммитить.
- Прогнать actionlint, shellcheck/bash syntax, Desktop suite, Control suite и релевантные acceptance/bootstrap suites.
- Сверить результаты GitHub CI. Зелёный CI без security-сценариев не считать достаточным.

Если найден блокер — не чинить молча в ветке Antigravity. Остановить merge, дать точное замечание с воспроизведением и дождаться исправления.

## Часть 2 — merge

Merge разрешён только если:

- все критерии Antigravity-задачи фактически выполнены;
- branch актуальна относительно `main`;
- локальные и GitHub gates зелёные;
- producer/consumer формат cosign доказан на реальном `alpha.9`;
- нет незаявленного workflow или release scope.

После merge повторить обязательные suites на merge commit. Не использовать старый CI head как единственное доказательство.

## Часть 3 — immutable release `v0.1.0-alpha.10`

- Использовать `linux-release.yml` как единственного публикатора.
- Если Queue B выявила producer-side gap (certificate/bundle/manifest metadata), исправить его отдельным reviewable PR до тега, с actionlint и тестом producer/consumer contract.
- Обновить tracked `deploy/smm-setup.sh` и прочие version sources на `v0.1.0-alpha.10` до создания тега.
- Создать новый tag только на принятом `main`. Не двигать `alpha.8` или `alpha.9`, не заменять их assets.
- Дождаться полного release pipeline.
- Проверить наличие и согласованность:
  - Linux x64/arm64 archives и checksums;
  - Windows MSIX и checksum;
  - SBOM для всех платформ;
  - manifest v2;
  - signature и необходимые verification materials;
  - bootstrap и checksum;
  - `smm-setup.sh` и checksum.
- Проверить, что manifest содержит hashes именно опубликованных assets и версии Control/Agent/helper.
- Проверить подпись с pinned issuer/identity внешней командой, затем пройти consumer verification Desktop/bootstrap.
- Выполнить smoke update с предыдущего релиза без реального production-развёртывания: корректный пакет принимается, tampered/foreign/missing signature и несовместимые версии отклоняются до установки.

## Критерий завершения

- Queue B смержена только после независимой приёмки.
- Merge commit имеет зелёные Linux/Windows и security/acceptance gates.
- `v0.1.0-alpha.10` указывает на подтверждённый commit и больше не изменяется.
- Release pipeline зелёный, полный набор assets опубликован.
- Desktop и bootstrap проверяют реальные `alpha.10` manifest/signature/hash.
- Отрицательные сценарии доказаны.
- Старые теги и assets не изменялись.

## Итоговый отчёт

Указать:

- PR Queue B и merge commit;
- все review findings и как они закрыты;
- локальные и CI-команды с результатами и ссылками;
- release workflow run;
- tag/commit `alpha.10`;
- список assets, hashes и результат cosign verification;
- smoke/negative checks;
- что не запускалось и почему;
- известные ограничения и следующий незавершённый этап.

