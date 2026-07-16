# Server Monitor Manager

[English](../../README.md) · [Русский](README.ru.md) · [Español](README.es.md) · [简体中文](README.zh-CN.md) · **हिन्दी** · [العربية](README.ar.md) · [Português](README.pt-BR.md) · [Français](README.fr.md) · [Deutsch](README.de.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [Türkçe](README.tr.md)

Server Monitor Manager एक हल्का, Windows-first अनुप्रयोग है जो Linux सर्वरों की निगरानी, सीधे SSH सत्र और सर्वरों के बीच सुरक्षित कनेक्शन का स्पष्ट नियंत्रण देता है। यह निजी इंफ्रास्ट्रक्चर और छोटे server fleet के लिए है, जहाँ भारी dashboard, Kubernetes या हर Node पर public API की जरूरत नहीं होती।

## विशेषताएँ

- CPU/load, memory, swap, disk, inode, network, uptime, latency, SSH और WireGuard metrics;
- profiles, groups, tags, favorites, alerts और छोटी local history;
- अलग Ed25519 SSH key जिसकी private key Windows device पर रहती है;
- Hub को private terminal key दिए बिना direct SSH terminal;
- public IP वाले एक Hub से star network; Nodes को केवल outbound access चाहिए;
- `AI agent → Home server:22` जैसे directional Links, जिन्हें अलग-अलग बंद किया जा सकता है;
- `/32`, TCP/UDP port, policy version और TTL restrictions;
- one-time enrollment, CSR, mTLS, role separation, idempotency और audit।

## संरचना और स्थापना

Windows client mTLS/HTTPS से ASP.NET Core 10 और SQLite Control Hub से जुड़ता है। Linux Agent केवल outbound session बनाता है। WireGuard data ले जाता है और Hub का nftables default रूप से transit रोकता है। Link केवल एक दिशा खोलता है; NAT के पीछे Home server को public IP नहीं चाहिए।

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Hub पर UDP `51820` और TCP `7443` खोलें, Node codes बनाएँ और दूसरे सर्वरों पर Node role चुनें। फिर `install-control-hub`, `control-code`, `control-device-code` और `install-control-agent` चलाएँ। Installer `amd64`/`arm64` चुनता है और SHA-256 जाँचता है।

## सुरक्षा और स्थिति

Shared root password नहीं है और Node की WireGuard private key Node से बाहर नहीं जाती। Monitoring, terminal, Agent, Operator और AI automation identities अलग हैं। SSH forced-command shell/PTY/forwarding नहीं देता; mTLS roles सीमित करता है; nftables केवल explicit Links स्वीकारता है; SQLite पहले desired state और audit सहेजता है।

`v0.1.0-alpha.3` testing release है। Current development branch में Windows client, Hub/Node installer, Links, mTLS, certificate revocation और re-enrollment, SQLite, audit, event stream तथा downsampling वाला सीमित offline buffer तैयार हैं। Reconnect reconciliation, 50–100 Node load test और signed Windows installer अभी बाकी हैं।

## लाइसेंस

Copyright 2026 ochenstarik-ui। यह परियोजना [Apache License 2.0](../../LICENSE) के अंतर्गत उपलब्ध है।

दस्तावेज़: [architecture](../architecture.md), [security](../security-model.md), [roadmap](../roadmap.md), [installer](../installer-contract.md)।
