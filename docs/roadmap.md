# План разработки

## Этап 0 — репозиторий и контракт

- [x] переименовать проект в Server Monitor Manager;
- [x] выбрать звёздную архитектуру Hub/Node для первого Mesh;
- [x] разделить monitoring identity, terminal identity и AI-agent identity;
- [x] описать направленные Links и kill switch;
- [x] выбрать лицензию;
- [x] добавить CI, форматирование, тесты и release checksum;
- [ ] объединить PR приложения и установщика в `main`.

## Этап 1 — Windows SSH MVP

- [x] создать packaged WinUI 3 приложение;
- [x] добавить адаптивный Overview и профили нескольких серверов;
- [x] генерировать отдельный Ed25519 SSH-ключ;
- [x] сохранять профили локально без паролей;
- [x] получать CPU/load, RAM, disk, uptime и latency;
- [x] собрать и реально запустить x64-приложение;
- [x] редактирование и удаление серверов;
- [x] единственный изменяемый Hub;
- [x] настоящие страницы Servers, Links, Sessions и Settings.

## Этап 2 — установщик Hub/Node

- [x] SSH forced-command для Ubuntu/Debian;
- [x] роли `hub` и `node`;
- [x] WireGuard Hub и исходящие Node-соединения;
- [x] постоянный nftables ACL на Hub;
- [x] список узлов и handshake-состояние;
- [x] явные зависимости `sudo`, `visudo` и `ping` для минимального Debian;
- [x] безопасные `update` и `rollback` с root-only backup;
- [x] полный `uninstall-monitor`, `uninstall-node` и `uninstall-hub`;
- [ ] интеграционный тест повторной установки и reboot.

## Этап 3 — безопасная регистрация

- [x] локальная генерация WireGuard-ключа на Node;
- [x] одноразовый enrollment token;
- [x] срок действия не более 10 минут;
- [x] атомарное погашение token;
- [x] отзыв и повторная регистрация Node;
- [x] подтверждение SHA-256 fingerprint Control CA Hub;
- [x] защита desktop SSH-ключа через DPAPI.

## Этап 4 — управляемые Links

- [x] направленные пары source → destination;
- [x] ручное добавление и удаление nftables ACL из Windows-клиента;
- [x] политики по целевому `/32`, TCP/UDP и порту;
- [x] TTL и автоматическое истечение;
- [x] состояния Connecting, Active, Disconnecting, Partial, Disabled и Failed;
- [x] версия политики и подтверждение применения на Hub;
- [x] обязательное отключение после reconnect;
- [x] локальный append-only JSONL-аудит операций Link;
- [x] интеграционные тесты Control kill switch, перезапуска процесса и частичного отказа helper;
- [ ] end-to-end тесты nftables и реального reboot вместе с Linux-установщиком.

## Этап 5 — мониторинг и терминал

- [x] swap, inode, network и состояние SSH/WireGuard;
- [x] предупреждения по диску, памяти, inode и недоступности;
- [x] автоматическое обновление каждые 30 секунд;
- [x] короткая локальная история до 240 точек на сервер;
- [x] встроенный график CPU, RAM и диска;
- [x] экспорт диагностики без секретов;
- [x] отдельный прямой SSH-терминал;
- [x] отдельная terminal identity и подтверждение пользователя;
- [x] отдельная automation identity для AI-агента.

## Этап 6 — постоянный control layer

- [x] самодостаточный single-file Linux agent для amd64/arm64;
- [x] SQLite inventory, policies, history и audit;
- [x] исходящие mTLS agent sessions;
- [x] защищённый Hub event stream для desktop client;
- [x] ограниченный локальный буфер и downsampling;
- [x] idempotency key и защита от replay;
- [x] тест нагрузки 100 Node на одном Hub (конкурентные heartbeat, inventory и replay в CI).

## Этап 7 — релиз и другие платформы

- [ ] подписанный Windows installer и GitHub Release;
- [ ] checksum Linux-установщика и бинарников;
- [ ] macOS и Linux desktop после стабилизации Core/API;
- [ ] Android/iOS companion clients;
- [ ] push-уведомления без административных секретов у push-провайдера.
