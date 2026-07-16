# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · **日本語** · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager は、Linux サーバーの監視、直接 SSH セッション、サーバー間の安全な接続を明示的に制御する軽量な Windows-first アプリケーションです。重い管理パネル、Kubernetes、各 Node の公開 API を必要としない個人インフラや小規模環境向けです。

## 主な機能

- CPU/load、メモリ、swap、ディスク、inode、ネットワーク、uptime、遅延、SSH、WireGuard の監視；
- プロファイル、グループ、タグ、お気に入り、警告、短期ローカル履歴；
- Windows 端末内に秘密鍵を保持する専用 Ed25519 SSH 鍵；
- Hub に秘密鍵を渡さない直接 SSH ターミナル；
- 公開 IP を持つ 1 台の Hub によるスター型ネットワーク。Node は外向き通信のみ；
- `AI エージェント → Home server:22` のような方向付き Link を個別に無効化；
- `/32`、TCP/UDP、ポート、ポリシーバージョン、TTL；
- ワンタイム登録、CSR、mTLS、ロール分離、冪等性、監査。

## 構成とインストール

Windows クライアントは mTLS/HTTPS で ASP.NET Core 10 + SQLite Control Hub に接続します。Linux Agent は外向きセッションだけを作成します。WireGuard がデータを運び、Hub の nftables は転送を既定で拒否します。Link は一方向で、NAT 内の家庭サーバーに公開 IP は不要です。

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Hub で UDP `51820` と TCP `7443` を開き、Node コードを作成して他のサーバーを Node として導入します。その後 `install-control-hub`、`control-code`、`control-device-code`、`install-control-agent` を使用します。インストーラーは `amd64`/`arm64` を選択し SHA-256 を検証します。

## セキュリティと状況

共有 root パスワードはなく、Node の WireGuard 秘密鍵は Node 外に出ません。monitoring、terminal、Agent、Operator、AI automation の ID は分離されています。SSH は shell/PTY/forwarding のない forced-command、mTLS はロール制限、nftables は明示 Link のみを許可し、SQLite は firewall 変更前に状態と監査を保存します。

`v0.1.0-alpha.2` はテスト版です。現在の開発ブランチには Windows client、Hub/Node installer、Links、mTLS、SQLite、監査、イベント、downsampling 付きの制限オフラインバッファが実装済みです。証明書失効、再接続調整、50–100 Node 負荷試験、署名付き Windows installer は今後の課題です。

## ライセンス

Copyright 2026 ochenstarik-ui。本プロジェクトは [Apache License 2.0](../../LICENSE) で提供されます。

資料：[アーキテクチャ](../architecture.md)、[セキュリティ](../security-model.md)、[ロードマップ](../roadmap.md)、[インストーラー](../installer-contract.md)。
