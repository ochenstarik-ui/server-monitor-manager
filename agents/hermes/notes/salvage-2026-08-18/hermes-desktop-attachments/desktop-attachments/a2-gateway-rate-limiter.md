Правила работы: прочитай `C:\Users\Ochenstarik\kagent-tasks\hermes-work-protocol.md`.

Файл: `services/gateway/src/main.rs`. Только он. Выполняется после задачи A1.

## Дефект

Структура `RateLimiter` объявлена, но нигде не создаётся. В changelog версия 0.8 заявлена
как «TOTP 2FA + rate limiter», при этом ограничение частоты запросов не подключено ни к
одному маршруту. В `docs/ARCHITECTURE.md` раздел 2 называет rate limiting обязанностью
Gateway.

Вторая проблема: `HashMap` клиентов растёт неограниченно. Каждый новый адрес добавляет
запись, которая никогда не удаляется.

## Что сделать

- подключить лимитер как middleware ко всем маршрутам;
- ключ клиента: значение заголовка `x-forwarded-for`, при отсутствии — адрес пира;
- при превышении лимита возвращать `429` с заголовком `Retry-After`;
- параметры из окружения: `GATEWAY_RATE_LIMIT_WINDOW_SECONDS` (по умолчанию 60) и
  `GATEWAY_RATE_LIMIT_MAX_REQUESTS` (по умолчанию 120);
- добавить вытеснение записей, у которых окно истекло, чтобы карта не росла бесконечно;
- снять `#[allow(dead_code)]`, добавленный в задаче A1;
- добавить тесты: запрос в пределах лимита проходит, запрос сверх лимита получает `429`,
  после истечения окна счётчик сбрасывается, запись устаревшего клиента вытесняется.

Значения по умолчанию вынеси в константы рядом с объявлением структуры.

## Критерий приёмки

```bash
cargo fmt --manifest-path services/gateway/Cargo.toml --check
cargo clippy --manifest-path services/gateway/Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path services/gateway/Cargo.toml
```

Вывод приложить к отчёту. В `CHANGELOG.md` добавить строку о подключении ограничителя
частоты в раздел `[Unreleased] / Added`.
