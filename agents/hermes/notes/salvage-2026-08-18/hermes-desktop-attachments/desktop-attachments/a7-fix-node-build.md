Правила работы: прочитай `C:\Users\Ochenstarik\kagent-tasks\hermes-work-protocol.md`.

Ветка: `wt/kagent-090-green-trunk`, PR #3. Файлы: `services/control-plane/package.json`,
`services/control-plane/tsconfig.json`, `services/control-plane/src/auth.test.ts`,
новый `services/control-plane/tsconfig.build.json`.

## Факт

PR #3 **не смержен**, `main` стоит на `3db581d`, сборка ветки красная:
`https://github.com/ochenstarik-ui/kagent/actions/runs/31101831510`

Jobs `rust` и `python` зелёные. Job `node` падает на шаге `pnpm build` с пятью ошибками
TS2352 в `services/control-plane/src/auth.test.ts` — небезопасные приведения типов:

```
src/auth.test.ts(30,46): Conversion of type '{ status: ... }' to type 'FastifyReply<...>'
  may be a mistake because neither type sufficiently overlaps with the other.
src/auth.test.ts(31,13): Conversion of type 'undefined' to type '{ code: number; }' ...
```

Строки 30, 31, 37, 38, 72.

## Почему это не поймали раньше

В `services/control-plane/package.json` **нет скрипта `typecheck`**. Корневой
`pnpm typecheck` выполняет `pnpm -r --if-present typecheck` и молча пропускает пакет.
В отчёте предыдущего исполнителя это прямо видно: «Scope: 3 of 4 workspace projects» —
пропущен ровно тот пакет, который правили.

Ошибки вскрылись только на `pnpm build`, потому что `build: "tsc"` компилирует и тестовые
файлы: в `tsconfig.json` нет исключения.

## Что сделать

1. Добавить в `services/control-plane/package.json` скрипт
   `"typecheck": "tsc --noEmit"`. Это закрывает дыру, из-за которой пакет выпадал из
   проверки.
2. Создать `services/control-plane/tsconfig.build.json`, наследующий `tsconfig.json` и
   исключающий `**/*.test.ts`; изменить `build` на `tsc -p tsconfig.build.json`.
   Тесты продолжают проверяться типами через `typecheck`, но не попадают в `dist`.
3. Переписать `auth.test.ts` без приведений через `as`:
   - заглушка ответа должна записывать статус и тело, а проверка — читать записанное, а не
     приводить возвращаемое значение middleware к `{ code: number }`;
   - для аргументов middleware использовать минимальные типизированные заглушки, а не
     `as Parameters<typeof authMiddleware>[N]`.

   Смысл проверок не менять: без заголовка 401, чужая схема 401, валидный токен
   прикрепляет принципала.

## Критерий приёмки

```bash
pnpm --filter @kagent/control-plane typecheck
pnpm --filter @kagent/control-plane test
pnpm typecheck
pnpm test
pnpm build
```

Все пять проходят, вывод приложить к отчёту. Проверить, что `pnpm typecheck` теперь
захватывает четыре пакета из четырёх, а не три.

После пуша дождаться прогона CI на PR #3 и приложить ссылку на **зелёный** прогон. Отчёт со
словами «должно пройти» не принимается: сборка проверяется прогоном, а не рассуждением.

## Границы

Ничего, кроме перечисленных четырёх файлов. Замену HTTP-клиента в gateway, зависимости и
что-либо ещё в этой карточке не трогать.
