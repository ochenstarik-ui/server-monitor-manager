# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · **Español** · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager es una aplicación ligera, inicialmente para Windows, que monitoriza servidores Linux, abre sesiones SSH directas y controla de forma explícita las conexiones seguras entre servidores. Está pensada para infraestructura personal y flotas pequeñas sin paneles pesados, Kubernetes ni una API pública en cada nodo.

## Funciones

- métricas de CPU/load, memoria, swap, discos, inodos, red, uptime, latencia, SSH y WireGuard;
- perfiles, grupos, etiquetas, favoritos, alertas e historial local corto;
- clave SSH Ed25519 dedicada que permanece en el equipo Windows;
- terminal SSH directo sin entregar la clave privada al Hub;
- red en estrella mediante un Hub con IP pública; los Nodes solo necesitan salida;
- Links direccionales como `agente IA → servidor doméstico:22`, desactivables por separado;
- políticas `/32`, TCP/UDP, puerto, versión y TTL;
- tokens de un solo uso, CSR, mTLS, roles separados, idempotencia y auditoría.

## Arquitectura

El cliente Windows usa mTLS/HTTPS con un Control Hub ASP.NET Core 10 y SQLite. El agente Linux solo inicia sesiones salientes. WireGuard transporta el tráfico y nftables lo bloquea por defecto. Un Link no concede acceso inverso ni acceso a otro servidor, y un servidor doméstico detrás de NAT no necesita IP pública.

## Instalación rápida

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Abra UDP `51820` y TCP `7443` en el Hub, cree códigos para los Nodes e instálelos con el rol Node. Después use `install-control-hub`, `control-code`, `control-device-code` e `install-control-agent`. El instalador selecciona `amd64`/`arm64` y verifica SHA-256.

## Seguridad y estado

No hay contraseña root compartida ni claves WireGuard privadas de Nodes en el Hub. Las identidades de monitorización, terminal, Agent, Operator y automatización IA están separadas. SSH usa un forced-command sin shell, PTY ni forwarding; mTLS limita cada rol; nftables permite únicamente Links explícitos; SQLite conserva estado y auditoría antes de aplicar cambios.

`v0.1.0-alpha.4` es una versión de prueba. La rama actual incluye cliente Windows, instalador Hub/Node, Links, mTLS, revocación y reinscripción de certificados, SQLite, auditoría, eventos y un búfer offline limitado con downsampling. Faltan la reconciliación tras reconexión, prueba de 50–100 Nodes e instalador Windows firmado.

## Licencia

Copyright 2026 ochenstarik-ui. El proyecto se distribuye bajo la [Licencia Apache 2.0](../../LICENSE).

Documentación: [arquitectura](../architecture.md), [seguridad](../security-model.md), [plan](../roadmap.md), [instalador](../installer-contract.md).
