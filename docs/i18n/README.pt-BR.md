# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · **Português** · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager é um aplicativo leve, inicialmente para Windows, que monitora servidores Linux, abre sessões SSH diretas e controla explicitamente conexões seguras entre servidores. Foi criado para infraestrutura pessoal e frotas pequenas sem painel pesado, Kubernetes ou API pública em cada Node.

## Recursos

- CPU/load, memória, swap, discos, inodes, rede, uptime, latência, SSH e WireGuard;
- perfis, grupos, tags, favoritos, alertas e histórico local curto;
- chave SSH Ed25519 dedicada, com a chave privada mantida no Windows;
- terminal SSH direto sem entregar a chave privada ao Hub;
- rede em estrela com um Hub de IP público; Nodes precisam apenas de acesso de saída;
- Links direcionais como `agente de IA → servidor doméstico:22`, desligados separadamente;
- políticas `/32`, TCP/UDP, porta, versão e TTL;
- token único, CSR, mTLS, separação de papéis, idempotência e auditoria.

## Arquitetura e instalação

O cliente Windows usa mTLS/HTTPS com um Control Hub ASP.NET Core 10 e SQLite. O Linux Agent inicia somente sessões de saída. WireGuard transporta dados e nftables bloqueia trânsito por padrão. Um Link não abre o sentido inverso; um servidor doméstico atrás de NAT não precisa de IP público.

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Abra UDP `51820` e TCP `7443` no Hub, gere códigos e instale os demais servidores como Node. Depois use `install-control-hub`, `control-code`, `control-device-code` e `install-control-agent`. O instalador escolhe `amd64`/`arm64` e valida SHA-256.

## Segurança e status

Não há senha root compartilhada e a chave WireGuard privada nunca sai do Node. As identidades de monitoramento, terminal, Agent, Operator e automação de IA são separadas. SSH usa forced-command sem shell/PTY/forwarding; mTLS restringe funções; nftables permite apenas Links explícitos; SQLite registra estado e auditoria antes da mudança no firewall.

`v0.1.0-alpha.2` é uma versão de teste. A branch de desenvolvimento atual já contém cliente Windows, instalador Hub/Node, Links, mTLS, SQLite, auditoria, eventos e buffer offline limitado com downsampling. Restam revogação de certificado, reconciliação, teste com 50–100 Nodes e instalador Windows assinado.

## Licença

Copyright 2026 ochenstarik-ui. O projeto é distribuído sob a [Licença Apache 2.0](../../LICENSE).

Documentação: [arquitetura](../architecture.md), [segurança](../security-model.md), [roadmap](../roadmap.md), [instalador](../installer-contract.md).
