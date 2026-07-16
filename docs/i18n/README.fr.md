# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · **Français** · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager est une application légère, d'abord conçue pour Windows, qui surveille des serveurs Linux, ouvre des sessions SSH directes et contrôle explicitement les connexions sécurisées entre serveurs. Elle vise les infrastructures personnelles et petites flottes sans tableau de bord lourd, Kubernetes ni API publique sur chaque nœud.

## Fonctionnalités

- CPU/load, mémoire, swap, disques, inodes, réseau, uptime, latence, SSH et WireGuard ;
- profils, groupes, tags, favoris, alertes et historique local court ;
- clé SSH Ed25519 dédiée dont la partie privée reste sur Windows ;
- terminal SSH direct sans transmettre la clé privée au Hub ;
- réseau en étoile via un Hub à IP publique ; les Nodes n'ont besoin que d'un accès sortant ;
- Links directionnels comme `agent IA → serveur maison:22`, désactivables séparément ;
- politiques `/32`, TCP/UDP, port, version et TTL ;
- enrôlement unique, CSR, mTLS, rôles séparés, idempotence et audit.

## Architecture et installation

Le client Windows communique en mTLS/HTTPS avec un Control Hub ASP.NET Core 10 et SQLite. L'Agent Linux ne crée que des sessions sortantes. WireGuard transporte les données et nftables bloque le transit par défaut. Un Link n'ouvre pas le sens inverse ; un serveur derrière NAT n'a pas besoin d'IP publique.

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Ouvrez UDP `51820` et TCP `7443` sur le Hub, créez les codes et installez les autres serveurs comme Nodes. Utilisez ensuite `install-control-hub`, `control-code`, `control-device-code` et `install-control-agent`. L'installateur choisit `amd64`/`arm64` et vérifie SHA-256.

## Sécurité et état

Aucun mot de passe root n'est partagé et la clé WireGuard privée reste sur le Node. Les identités monitoring, terminal, Agent, Operator et automatisation IA sont séparées. SSH emploie une forced-command sans shell/PTY/forwarding ; mTLS limite les rôles ; nftables n'autorise que les Links explicites ; SQLite enregistre état et audit avant le changement de pare-feu.

`v0.1.0-alpha.2` est destiné aux tests. La branche de développement actuelle contient déjà le client Windows, l'installateur Hub/Node, les Links, mTLS, SQLite, l'audit, les événements et un tampon hors ligne limité avec downsampling. Restent la révocation des certificats, la réconciliation, le test de 50–100 Nodes et l'installateur Windows signé.

## Licence

Copyright 2026 ochenstarik-ui. Le projet est distribué sous [licence Apache 2.0](../../LICENSE).

Documentation : [architecture](../architecture.md), [sécurité](../security-model.md), [roadmap](../roadmap.md), [installateur](../installer-contract.md).
