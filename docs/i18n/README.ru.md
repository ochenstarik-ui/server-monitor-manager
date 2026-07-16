# Server Monitor Manager

[English](../../README.md) · **Русский** · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager — лёгкое Windows-приложение для мониторинга Linux-серверов, прямых SSH-сессий и явного управления защищёнными соединениями между серверами. Оно рассчитано на личную инфраструктуру и небольшие парки серверов без тяжёлой панели, Kubernetes или публичного API на каждом узле.

## Возможности

- мониторинг CPU/load, RAM, swap, дисков, inode, сети, uptime, задержки, SSH и WireGuard;
- профили, группы, теги, избранное, предупреждения и короткая история метрик;
- отдельный Ed25519 SSH-ключ, который остаётся на Windows-компьютере;
- прямой SSH-терминал без передачи приватного terminal-ключа Hub-серверу;
- объединение серверов через один Hub с белым IP; вторичным серверам нужен только исходящий доступ;
- направленные Links, например `AI-агент → домашний сервер:22`, с независимым отключением;
- ограничения по `/32`, TCP/UDP-порту, версии политики и TTL;
- одноразовая регистрация, CSR, mTLS, разделение ролей, idempotency и аудит.

## Архитектура

Windows-клиент подключается по mTLS/HTTPS к Control Hub на ASP.NET Core 10 и SQLite. Linux Agent создаёт только исходящие mTLS-сессии. WireGuard образует звёздную сеть, а nftables на Hub по умолчанию запрещает транзит. Link направленный: доступ `AI-агент → Home` не открывает обратное направление или другие серверы. Домашнему серверу за NAT не нужен белый IP.

Control plane хранит inventory, метрики, политики, историю и аудит. Data plane состоит из WireGuard и постоянных nftables ACL. Operator-сертификат Windows защищён DPAPI; Agent доступен для `amd64` и `arm64` как самодостаточный Linux-бинарник.

## Быстрая установка

Установщик находится в [`ochenstarik-ui/lightweight-server`](https://github.com/ochenstarik-ui/lightweight-server):

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

На Hub откройте UDP `51820` и TCP `7443`, создайте `node-code`, затем установите вторичные серверы в роли Node. Для постоянного слоя выполните `install-control-hub`, создайте `control-code`/`control-device-code`, а на Node — `install-control-agent`. Архитектура выбирается автоматически, SHA-256 проверяется.

## Безопасность и статус

Нет общих root-паролей; приватный WireGuard-ключ Node не покидает Node. Идентичности monitoring, terminal, Agent, Operator и AI-автоматизации разделены. Monitoring SSH использует forced-command без shell/PTY/forwarding. Agent может отправлять heartbeat только своего узла, Operator управляет inventory и Links. Отключение Link сначала сохраняется в SQLite, затем удаляет разрешение nftables; повтор запроса не повторяет побочный эффект.

`v0.1.0-alpha.2` предназначен для тестирования. В текущей ветке разработки уже готовы Windows SSH-monitoring, Hub/Node installer, Links, mTLS, SQLite, аудит, поток событий и ограниченный offline-буфер Agent с downsampling. Остались отзыв сертификатов, reconnect reconciliation, нагрузочный тест 50–100 Node и подписанный Windows installer. До стабильного релиза используйте тестовые или резервируемые серверы.

## Лицензия

Copyright 2026 ochenstarik-ui. Проект распространяется по [Apache License 2.0](../../LICENSE).

Документы: [архитектура](../architecture.md), [безопасность](../security-model.md), [roadmap](../roadmap.md), [контракт установщика](../installer-contract.md).
