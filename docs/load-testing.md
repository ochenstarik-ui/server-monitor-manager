# Нагрузочный тест Hub

Цель текущего теста — постоянно проверять, что один Control Hub корректно принимает штатную нагрузку от 100 Node и сохраняет семантику idempotency. Это регрессионный CI-тест, а не синтетический рейтинг производительности конкретного процессора.

## Сценарий

`HubLoadTests.OneHubAcceptsConcurrentHeartbeatsFromOneHundredNodes` выполняет следующие действия:

1. создаёт отдельную SQLite-базу Hub;
2. регистрирует 100 Node с разными сертификатами;
3. отправляет от всех Node три одновременные волны heartbeat;
4. ограничивает каждую волну штатным интервалом Agent в 30 секунд;
5. повторяет последнюю волну с теми же idempotency key;
6. проверяет 100 online-узлов, ровно 300 metric samples и неизменные sequence при replay.

Таким образом CI проверяет худший случай синхронного всплеска вместо распределения heartbeat по 30-секундному окну.

## Запуск

```bash
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj \
  --configuration Release \
  --filter "Category=Load"
```

Тест охватывает конкурентный путь Control/SQLite. Реальные WireGuard, nftables, пропускная способность сети, TLS handshake и перезагрузка хоста относятся к отдельному инфраструктурному end-to-end тесту установщика и не имитируются этим сценарием.
