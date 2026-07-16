# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · **Türkçe**

Server Monitor Manager, Linux sunucularını izleyen, doğrudan SSH oturumları açan ve sunucular arasındaki güvenli bağlantıları açıkça yöneten hafif, Windows öncelikli bir uygulamadır. Ağır panel, Kubernetes veya her Node üzerinde herkese açık API gerektirmeyen kişisel altyapılar ve küçük filolar için tasarlanmıştır.

## Özellikler

- CPU/load, bellek, swap, disk, inode, ağ, uptime, gecikme, SSH ve WireGuard izleme;
- profiller, gruplar, etiketler, favoriler, uyarılar ve kısa yerel geçmiş;
- özel anahtarı Windows cihazında kalan ayrı Ed25519 SSH anahtarı;
- özel terminal anahtarını Hub'a vermeden doğrudan SSH terminali;
- genel IP'li tek Hub üzerinden yıldız ağ; Nodes yalnızca dış bağlantıya ihtiyaç duyar;
- `AI ajanı → ev sunucusu:22` gibi bağımsız kapatılabilen yönlü Links;
- `/32`, TCP/UDP, port, politika sürümü ve TTL sınırları;
- tek kullanımlık kayıt, CSR, mTLS, rol ayrımı, idempotency ve audit.

## Mimari ve kurulum

Windows istemcisi mTLS/HTTPS ile ASP.NET Core 10 ve SQLite Control Hub'a bağlanır. Linux Agent yalnızca dış oturum açar. WireGuard veriyi taşır, Hub üzerindeki nftables varsayılan olarak geçişi engeller. Link tek yönlüdür; NAT arkasındaki ev sunucusunun genel IP'ye ihtiyacı yoktur.

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Hub üzerinde UDP `51820` ve TCP `7443` açın, Node kodlarını üretin ve diğer sunucuları Node olarak kurun. Ardından `install-control-hub`, `control-code`, `control-device-code` ve `install-control-agent` kullanın. Kurucu `amd64`/`arm64` seçer ve SHA-256 doğrular.

## Güvenlik ve durum

Ortak root parolası yoktur ve Node'un WireGuard özel anahtarı Node'dan çıkmaz. Monitoring, terminal, Agent, Operator ve AI automation kimlikleri ayrıdır. SSH shell/PTY/forwarding vermeyen forced-command kullanır; mTLS rolleri sınırlar; nftables yalnızca açık Links'e izin verir; SQLite güvenlik duvarı değişmeden önce durum ve audit kaydeder.

`v0.1.0-alpha.4` test sürümüdür. Güncel geliştirme dalında Windows client, Hub/Node installer, Links, mTLS, sertifika iptali ve yeniden kayıt, SQLite, audit, event stream ve downsampling kullanan sınırlı offline buffer hazırdır. Yeniden bağlantı uzlaştırması, 50–100 Node yük testi ve imzalı Windows installer sıradadır.

## Lisans

Copyright 2026 ochenstarik-ui. Proje [Apache License 2.0](../../LICENSE) altında dağıtılır.

Belgeler: [mimari](../architecture.md), [güvenlik](../security-model.md), [yol haritası](../roadmap.md), [kurucu sözleşmesi](../installer-contract.md).
