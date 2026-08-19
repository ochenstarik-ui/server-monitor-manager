Правила работы: прочитай `C:\Users\Ochenstarik\kagent-tasks\hermes-work-protocol.md`.

Файлы: `services/control-plane/src/main.ts`, `services/control-plane/src/auth.ts`,
при необходимости `services/control-plane/package.json`. Тесты в этой задаче не пишутся —
они в задаче A4.

## Дефект 1: сервис не стартует

В `main.ts` логгер сконфигурирован с `transport.target: "pino-pretty"`, а пакета
`pino-pretty` нет в зависимостях. Fastify падает при инициализации.

Предпочтительное решение: убрать `transport` и оставить структурный JSON-вывод. В
контейнере человекочитаемый вывод не нужен, а зависимость попадёт в образ. Если выберешь
добавить `pino-pretty` в `devDependencies` и включать транспорт только вне продакшена —
обоснуй в отчёте.

## Дефект 2: регистрация и вход не работают

В `auth.ts` функция `pbkdf2` получает модуль через `require("node:crypto")`. Пакет объявлен
как `"type": "module"`, поэтому `require` не определён и вызов бросает `ReferenceError` при
каждом хешировании пароля. То есть `POST /v1/auth/register` и `POST /v1/auth/login` не
работают вообще.

В первой строке файла уже есть статический импорт
`import { randomBytes, createHash, timingSafeEqual } from "node:crypto"`. Добавь в него
`pbkdf2` и убери динамическое получение модуля.

## Дефект 3: испорченный хеш даёт 500 вместо 401

`verifyPassword` разбирает хранимую строку через `split(":")` и передаёт результат в
`timingSafeEqual`, который бросает исключение при разной длине буферов. Испорченное или
устаревшее значение в базе превращается в 500.

Разбор должен проверять формат и возвращать `false`, а не бросать исключение. Сравнение
остаётся постоянного времени.

## Критерий приёмки

```bash
pnpm --filter @kagent/control-plane typecheck
```

Проходит без ошибок при включённых `strict`, `noUncheckedIndexedAccess`,
`exactOptionalPropertyTypes`. Тип `any` не использовать.

Сервис запускается: `pnpm --filter @kagent/control-plane dev` поднимается и отвечает на
`GET /health/live`. Если PostgreSQL недоступен — это ожидаемо на данном этапе, важно, что
процесс не падает при старте. Вывод приложить к отчёту.

Изменения зависимостей перечислить отдельно с указанием лицензии.
