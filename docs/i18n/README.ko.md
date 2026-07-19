# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · [हिन्दी](README.hi.md) · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · **한국어** · [Türkçe](README.tr.md)

Server Monitor Manager는 Linux 서버 모니터링, 직접 SSH 세션, 서버 간 보안 연결의 명시적 제어를 제공하는 가벼운 Windows 우선 애플리케이션입니다. 무거운 대시보드, Kubernetes 또는 각 Node의 공개 API가 필요 없는 개인 인프라와 소규모 서버 환경을 위한 도구입니다.

## 주요 기능

- CPU/load, 메모리, swap, 디스크, inode, 네트워크, uptime, 지연, SSH, WireGuard 모니터링;
- 프로필, 그룹, 태그, 즐겨찾기, 경고와 짧은 로컬 기록;
- 개인 키가 Windows 장치에만 남는 전용 Ed25519 SSH 키;
- 개인 terminal 키를 Hub에 전달하지 않는 직접 SSH 터미널;
- 공개 IP가 있는 하나의 Hub를 통한 스타 네트워크, Node는 outbound 연결만 필요;
- `AI 에이전트 → 홈 서버:22` 같은 방향성 Link를 독립적으로 해제;
- `/32`, TCP/UDP, 포트, 정책 버전, TTL 제한;
- 일회용 등록, CSR, mTLS, 역할 분리, 멱등성 및 감사.

## 구조와 설치

Windows client는 mTLS/HTTPS로 ASP.NET Core 10 및 SQLite Control Hub에 연결합니다. Linux Agent는 outbound session만 만듭니다. WireGuard가 데이터를 전달하고 Hub의 nftables는 기본적으로 transit을 거부합니다. Link는 단방향이며 NAT 뒤의 홈 서버에는 공개 IP가 필요 없습니다.

```bash
# 먼저 릴리스 파일에서 ochenstarik-server-monitor-manager.sh를 다운로드하세요.
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Hub에서 UDP `51820`과 TCP `7443`을 열고 Node 코드를 생성한 뒤 다른 서버를 Node로 설치합니다. 이후 `install-control-hub`, `control-code`, `control-device-code`, `install-control-agent`를 사용합니다. 설치 프로그램은 `amd64`/`arm64`를 선택하고 SHA-256을 검증합니다.

## 보안과 상태

공유 root 암호가 없고 Node WireGuard 개인 키는 Node를 떠나지 않습니다. monitoring, terminal, Agent, Operator, AI automation identity는 분리됩니다. SSH는 shell/PTY/forwarding 없는 forced-command를 사용하고, mTLS는 역할을 제한하며, nftables는 명시된 Link만 허용합니다. SQLite는 방화벽 변경 전에 상태와 감사를 저장합니다.

`v0.1.0-alpha.4`는 테스트 릴리스입니다. 현재 개발 branch에는 Windows client, Hub/Node installer, Links, mTLS, 인증서 폐기와 재등록, SQLite, 감사, event stream과 downsampling이 적용된 제한 offline buffer가 구현되었습니다. 재연결 조정, 50–100 Node 부하 시험과 서명된 Windows installer가 남아 있습니다.

## 라이선스

Copyright 2026 ochenstarik-ui. 이 프로젝트는 [Apache License 2.0](../../LICENSE)으로 배포됩니다.

문서: [아키텍처](../architecture.md), [보안](../security-model.md), [로드맵](../roadmap.md), [설치 계약](../installer-contract.md).
