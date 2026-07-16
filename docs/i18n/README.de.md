# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · **Deutsch** · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager ist eine schlanke, zunächst für Windows entwickelte Anwendung zur Überwachung von Linux-Servern, für direkte SSH-Sitzungen und zur ausdrücklichen Steuerung sicherer Serververbindungen. Sie richtet sich an private Infrastruktur und kleine Flotten ohne schwergewichtiges Dashboard, Kubernetes oder öffentliche API auf jedem Node.

## Funktionen

- CPU/load, Speicher, Swap, Datenträger, Inodes, Netzwerk, Uptime, Latenz, SSH und WireGuard;
- Profile, Gruppen, Tags, Favoriten, Warnungen und kurze lokale Historie;
- eigener Ed25519-SSH-Schlüssel, dessen privater Teil auf Windows bleibt;
- direktes SSH-Terminal ohne privaten Schlüssel auf dem Hub;
- Sternnetz über einen Hub mit öffentlicher IP; Nodes benötigen nur ausgehenden Zugriff;
- gerichtete Links wie `KI-Agent → Home-Server:22`, einzeln abschaltbar;
- `/32`-, TCP/UDP-, Port-, Versions- und TTL-Regeln;
- Einmal-Enrollment, CSR, mTLS, Rollentrennung, Idempotenz und Audit.

## Architektur und Installation

Der Windows-Client kommuniziert per mTLS/HTTPS mit einem ASP.NET Core 10 Control Hub und SQLite. Der Linux Agent baut nur ausgehende Sitzungen auf. WireGuard transportiert Daten, nftables sperrt Transit standardmäßig. Ein Link öffnet keine Gegenrichtung; ein Server hinter NAT benötigt keine öffentliche IP.

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Öffnen Sie UDP `51820` und TCP `7443` am Hub, erzeugen Sie Codes und installieren Sie weitere Server als Node. Danach folgen `install-control-hub`, `control-code`, `control-device-code` und `install-control-agent`. Der Installer wählt `amd64`/`arm64` und prüft SHA-256.

## Sicherheit und Status

Es gibt kein gemeinsames Root-Passwort; der private WireGuard-Schlüssel bleibt auf dem Node. Monitoring-, Terminal-, Agent-, Operator- und KI-Automationsidentitäten sind getrennt. SSH nutzt einen forced-command ohne Shell/PTY/Forwarding; mTLS begrenzt Rollen; nftables erlaubt nur explizite Links; SQLite speichert Sollzustand und Audit vor der Firewalländerung.

`v0.1.0-alpha.2` ist eine Testversion. Der aktuelle Entwicklungszweig enthält bereits Windows-Client, Hub/Node-Installer, Links, mTLS, SQLite, Audit, Events und einen begrenzten Offline-Puffer mit Downsampling. Offen sind Zertifikatswiderruf, Reconnect-Abgleich, Lasttest mit 50–100 Nodes und signierter Windows-Installer.

## Lizenz

Copyright 2026 ochenstarik-ui. Das Projekt steht unter der [Apache License 2.0](../../LICENSE).

Dokumentation: [Architektur](../architecture.md), [Sicherheit](../security-model.md), [Roadmap](../roadmap.md), [Installer](../installer-contract.md).
