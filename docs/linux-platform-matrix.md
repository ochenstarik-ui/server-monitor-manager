# Linux platform matrix

Workflow `linux-platform-matrix.yml` собирает те же self-contained release-архивы, которые устанавливает production bootstrap, отдельно для `linux-x64` и `linux-arm64`.

## Реальные Ubuntu VM

На GitHub-hosted VM проверяются:

- Ubuntu 22.04 x64;
- Ubuntu 24.04 x64;
- Ubuntu 22.04 arm64;
- Ubuntu 24.04 arm64.

Каждая VM выполняет `preflight`, проверяет checksum и содержимое архива, дважды устанавливает Control, проверяет emergency-команду, перезапускает systemd service и обращается к HTTPS `/healthz` через созданный CA.

## Debian systemd containers

Debian 12/13 проверяются для x64 и arm64 в privileged systemd-контейнерах на соответствующей архитектуре runner. Тест выполняет чистую и повторную установку, затем полностью перезапускает контейнер и проверяет автоматический запуск Control и HTTPS healthcheck после нового systemd boot.

Systemd-контейнер проверяет дистрибутив, users, permissions, units, certificates, SQLite state и service lifecycle, но не считается полноценной Debian VM. Отдельным незакрытым критерием остаётся reboot настоящих Debian VM с собственным kernel. Физическая проверка WireGuard/nftables также выполняется по `three-server-acceptance.md`, поскольку GitHub runners не моделируют внешний NAT и cloud firewall.
