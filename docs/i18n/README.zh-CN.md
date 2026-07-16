# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · **简体中文** · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager 是一款轻量级、Windows 优先的 Linux 服务器监控与管理应用。它支持直接 SSH 会话，并能明确控制服务器之间的安全连接，适合个人基础设施和小型服务器集群，无需笨重面板、Kubernetes，也无需在每个节点暴露公共 API。

## 主要功能

- 监控 CPU/load、内存、swap、磁盘、inode、网络、uptime、延迟、SSH 和 WireGuard；
- 管理配置、分组、标签、收藏、告警和短期本地历史；
- 生成专用 Ed25519 SSH 密钥，私钥仅保存在 Windows 设备；
- 直接 SSH 终端，不向 Hub 传输终端私钥；
- 通过一个具有公网 IP 的 Hub 连接服务器，其他 Node 只需出站访问；
- 创建 `AI 代理 → 家庭服务器:22` 等单向 Link，并可单独关闭；
- 支持 `/32`、TCP/UDP、端口、策略版本和 TTL；
- 一次性令牌、CSR、mTLS、角色隔离、幂等和审计。

## 架构

Windows 客户端通过 mTLS/HTTPS 连接 ASP.NET Core 10 + SQLite Control Hub。Linux Agent 只建立出站会话。WireGuard 负责数据传输，Hub 上的 nftables 默认拒绝节点间转发。Link 是单向的，不自动开放反向或其他服务器访问；NAT 后的家庭服务器无需公网 IP。

## 快速安装

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

在 Hub 开放 UDP `51820` 和 TCP `7443`，创建 Node 注册码并在其他服务器选择 Node 角色。随后运行 `install-control-hub`、`control-code`、`control-device-code` 和 `install-control-agent`。安装器会自动选择 `amd64`/`arm64` 并校验 SHA-256。

## 安全与状态

系统不共享 root 密码，Node 的 WireGuard 私钥不会离开本机。监控、终端、Agent、Operator 和 AI 自动化身份相互隔离。SSH 使用无 shell、PTY、转发权限的 forced-command；mTLS 限制角色；nftables 仅允许明确 Link；SQLite 在执行防火墙变更前保存目标状态和审计。

`v0.1.0-alpha.2` 是测试版，已包含 Windows 客户端、Hub/Node 安装器、Links、mTLS、SQLite、审计和事件流。待完成：离线缓冲、证书撤销、重连协调、50–100 Node 压测和签名 Windows 安装器。

## 许可证

Copyright 2026 ochenstarik-ui。本项目采用 [Apache License 2.0](../../LICENSE)。

文档：[架构](../architecture.md)、[安全模型](../security-model.md)、[路线图](../roadmap.md)、[安装器协议](../installer-contract.md)。
