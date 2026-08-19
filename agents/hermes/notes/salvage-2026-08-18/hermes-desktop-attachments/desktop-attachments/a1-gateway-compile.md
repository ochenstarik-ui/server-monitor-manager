Правила работы: прочитай `C:\Users\Ochenstarik\kagent-tasks\hermes-work-protocol.md`.

Файл: `services/gateway/src/main.rs`. Только он.

## Дефект

Функция `get_request_id` вызывает `.or_else()` у значения типа `String`:

```rust
headers
    .get("x-request-id")
    .and_then(|v| v.to_str().ok())
    .unwrap_or("")
    .to_string()
    .or_else(|| { ... })
```

У `String` такого метода нет — ошибка компиляции E0599. Крейт не собирается.

Требуемое поведение: вернуть значение заголовка `x-request-id`, если он присутствует и
непустой; иначе сгенерировать UUID v4.

## Что сделать

- переписать `get_request_id` корректно;
- обработчик `live` принимает `State`, но не использует его: либо использовать, либо убрать
  из сигнатуры;
- привести файл к формату rustfmt.

`RateLimiter` в этой задаче **не трогать** — он не используется и вызовет предупреждение
`dead_code`. Чтобы clippy прошёл, добавь ему временно `#[allow(dead_code)]` с комментарием
`// подключается в задаче A2` и ничего больше в нём не меняй.

## Критерий приёмки

Все три команды проходят, вывод приложить к отчёту:

```bash
cargo fmt --manifest-path services/gateway/Cargo.toml --check
cargo clippy --manifest-path services/gateway/Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path services/gateway/Cargo.toml
```

Существующие тесты `parses_valid_port` и `rejects_zero_and_invalid_ports` должны проходить
без изменений.
