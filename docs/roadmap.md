# План разработки

## Этап 0 — репозиторий и контракт

- [x] переименовать проект в Server Monitor Manager;
- [x] выбрать звёздную архитектуру Hub/Node для первого Mesh;
- [x] разделить monitoring identity, terminal identity и AI-agent identity;
- [x] описать направленные Links и kill switch;
- [ ] выбрать лицензию;
- [ ] добавить CI, форматирование, тесты и release checksum;
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
- [ ] настоящие страницы Servers, Links, Sessions и Settings.

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
- [ ] отзыв и повторная регистрация Node;
- [ ] подтверждение fingerprint Hub;
- [ ] защита desktop SSH-ключа через DPAPI.

## Этап 4 — управляемые Links

- [x] направленные пары source → destination;
- [x] ручное добавление и удаление nftables ACL из Windows-клиента;
- [x] политики по целевому `/32`, TCP/UDP и порту;
- [x] TTL и автоматическое истечение;
- [ ] состояния Connecting, Active, Disconnecting, Partial, Disabled и Failed;
- [x] версия политики и подтверждение применения на Hub;
- [ ] обязательное отключение после reconnect;
- [x] локальный append-only JSONL-аудит операций Link;
- [ ] интеграционные тесты kill switch и частичных отказов.

## Этап 5 — мониторинг и терминал

- [x] swap, inode, network и состояние SSH/WireGuard;
- [x] предупреждения по диску, памяти, inode и недоступности;
- [x] автоматическое обновление каждые 30 секунд;
- [x] короткая локальная история до 240 точек на сервер;
- [x] встроенный график CPU, RAM и диска;
- [ ] экспорт диагностики без секретов;
- [ ] отдельный прямой SSH-терминал;
- [ ] отдельная terminal identity и подтверждение пользователя;
- [ ] отдельная automation identity для AI-агента.

## Этап 6 — постоянный control layer

- [ ] статический Linux agent для amd64/arm64;
- [ ] SQLite inventory, policies, history и audit;
- [ ] исходящие mTLS agent sessions;
- [ ] WebSocket/stream событий для desktop client;
- [ ] ограниченный локальный буфер и downsampling;
- [ ] idempotency key и защита от replay;
- [ ] тест нагрузки 50–100 Node на одном Hub.

## Этап 7 — релиз и другие платформы

- [ ] подписанный Windows installer и GitHub Release;
- [ ] checksum Linux-установщика и бинарников;
- [ ] macOS и Linux desktop после стабилизации Core/API;
- [ ] Android/iOS companion clients;
- [ ] push-уведомления без административных секретов у push-провайдера.
