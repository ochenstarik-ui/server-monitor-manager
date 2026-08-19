# Antigravity: покрыть тестом отдачу консоли из ресурсов сборки

## Репозиторий

- https://github.com/ochenstarik-ui/server-monitor-manager
- Ветка: `antigravity/web-console`, открытый PR **#59**
- Работа делается **в этом же PR**, новый не открывать
- `main` защищён: PR обязателен, обязательны статусы `build-and-test` и `build`

## Задача

Одна проверка. Ничего в поведении консоли менять не нужно.

`GetWebConsoleAsset` в `src/ServerMonitorManager.Control/Program.cs` берёт файл из двух источников по очереди:

1. с диска, по пути `IWebHostEnvironment.WebRootPath`;
2. если файла там нет — из ресурсов сборки, через `GetManifestResourceNames()` и поиск по `EndsWith(fileName)`.

Тесты поднимают хост в процессе, где `WebRootPath` существует и файлы лежат на диске. Значит проверяется **первая** ветка.

Control публикуется с `PublishSingleFile=true`, каталога `wwwroot` рядом с исполняемым файлом на сервере нет. Значит на настоящей машине работает **вторая** ветка — та, которую не проверяет ничто.

## Что сделать

Добавить в `tests/ServerMonitorManager.Control.Tests/WebConsoleTests.cs` тест, который поднимает хост с несуществующим или пустым `WebRootPath` и убеждается, что при обращении с сертификатом роли `Operator`:

- `/`, `/index.html`, `/style.css` и `/app.js` отвечают кодом 200;
- `Content-Type` соответствует типу файла;
- тело ответа непустое и содержит опознаваемый фрагмент: для HTML — предупреждение о сверке отпечатка, для JS — обращение к `/api/v1/control/agents`.

Побочная польза, ради которой это и стоит делать: тест зафиксирует соглашение об именах ресурсов. Поиск идёт через `EndsWith(fileName)`, поэтому переименование каталога `wwwroot` или смена схемы имён сломает отдачу консоли молча, без ошибки сборки.

## Границы

**Не трогать:** `deploy/**`, `.github/workflows/linux-*.yml`, `.github/workflows/release-verification.yml`, `tests/release-verification/**`, `tests/bootstrap/**`.

Ваша область: `tests/ServerMonitorManager.Control.Tests/**`. Если для подмены `WebRootPath` потребуется правка в `src/ServerMonitorManager.Control/**` — допустимо, но опишите зачем.

## Критерий приёмки

- тест поднимает хост без каталога `wwwroot` на диске и получает все четыре ресурса;
- существующие проверки ролей продолжают проходить;
- CI зелёный, PR не смержен до зелёного.

## Отчёт

В описании PR. Отдельным файлом отчёт не является.
