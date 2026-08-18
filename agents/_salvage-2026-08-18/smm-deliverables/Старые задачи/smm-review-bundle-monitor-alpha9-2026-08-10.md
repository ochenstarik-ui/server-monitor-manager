# Independent review bundle
## Full authoritative task

1|# Hermes: формат снимка Monitor, совместимость БД, дисциплина релизов
2|
3|## Состояние
4|
5|- `main` @ `f9205c3`, роль Monitor слита (#29), подписанная поставка Queue A слита (#26);
6|- `v0.1.0-alpha.8` пересобран, все Linux-артефакты и `server-monitor-manager-manifest.sig` на месте;
7|- открыт PR #31 с починкой путей Windows-артефактов.
8|
9|Ветка: `hermes/monitor-snapshot-contract`.
10|
11|---
12|
13|## Часть 1 — снимок Monitor не читается Desktop · блокирующий
14|
15|Роль Monitor установлена, но мониторинг работать не будет. Скрипт `/usr/local/libexec/ochenstarik-smm-metrics` выдаёт имена полей, которых не понимает ни парсер Desktop, ни собственный контракт проекта.
16|
17|**Ждёт `SshMonitorService.QueryAsync`:**
18|
19|```
20|PROTOCOL HOSTNAME UPTIME_SECONDS LOAD1 CPU_COUNT
21|MEM_TOTAL_KB MEM_AVAILABLE_KB SWAP_TOTAL_KB SWAP_FREE_KB
22|DISK_TOTAL_KB DISK_AVAILABLE_KB DISK_INODES_TOTAL DISK_INODES_FREE
23|NETWORK_RX_BYTES NETWORK_TX_BYTES SYSTEMD_SSH SYSTEMD_WIREGUARD
24|```
25|
26|**Выдаёт скрипт:**
27|
28|```
29|CPU_COUNT LOAD1 MEM_TOTAL MEM_AVAIL SWAP_TOTAL SWAP_FREE
30|DISK_TOTAL DISK_AVAIL INODES_TOTAL INODES_FREE
31|NET_RX NET_TX UPTIME KERNEL DF_OUT DF_INODES MESH_STATUS
32|```
33|
34|Совпадают **два поля из шестнадцати** — `CPU_COUNT` и `LOAD1`. Остальные не совпадают по имени, а `PROTOCOL`, `HOSTNAME`, `SYSTEMD_SSH`, `SYSTEMD_WIREGUARD` отсутствуют вовсе.
35|
36|Практический результат: в Desktop имя сервера подставится из профиля, память, диск, swap, inode и сеть покажут нули, состояния SSH и WireGuard — `unknown`. Формально роль установлена, фактически функция, ради которой существует Desktop, не работает.
37|
38|Формат уже зафиксирован в `docs/installer-contract.md` §7 — его и надо соблюсти дословно, а не изобретать заново.
39|
40|### Что сделать
41|
42|1. Привести вывод скрипта к `docs/installer-contract.md` §7: `PROTOCOL=1`, `HOSTNAME`, `UPTIME_SECONDS`, `LOAD1`, `CPU_COUNT`, `MEM_TOTAL_KB`, `MEM_AVAILABLE_KB`, `SWAP_TOTAL_KB`, `SWAP_FREE_KB`, `DISK_TOTAL_KB`, `DISK_AVAILABLE_KB`, `DISK_INODES_TOTAL`, `DISK_INODES_FREE`, `NETWORK_RX_BYTES`, `NETWORK_TX_BYTES`, `KERNEL`.
43|2. Добавить `SYSTEMD_SSH` и `SYSTEMD_WIREGUARD` — их читает Desktop, но в §7 их нет. Дописать в §7, чтобы контракт снова был единственным источником.
44|3. Внутренние переменные вроде `DF_OUT` и `DF_INODES` из вывода убрать: снимок не должен содержать ничего, кроме полей контракта.
45|
46|### Контрактный тест — переделать
47|
48|`tests/acceptance/test_monitor_snapshot.sh` проверяет скрипт против списка полей, который живёт в самом тесте. Именно поэтому расхождение с Desktop не поймалось.
49|
50|Список ожидаемых полей должен существовать **в одном месте** и использоваться обеими сторонами:
51|
52|- тестом shell-скрипта — что все поля присутствуют и лишних нет;
53|- тестом парсера Desktop — что он читает ровно эти поля.
54|
55|Как реализовать — на ваше усмотрение: общий файл-фикстура с эталонным снимком, из которого обе стороны берут набор ключей, либо генерация списка из `installer-contract.md`. Требование одно: **изменение имени поля с одной стороны обязано ронять тест другой стороны.**
56|
57|`SshMonitorService.cs` при этом не менять — правится скрипт, а не парсер. Если парсер тоже потребует правки, описать в `REPORT.md`.
58|
59|---
60|
61|## Часть 2 — совместимость существующей БД с SQLitePCLRaw 3.0.5
62|
63|Пункт из прошлого задания, оставшийся невыполненным. Сейчас он не горит только по счастливой случайности: `alpha.8` собран из коммита, где ещё `2.1.12`. Но в `main` уже `3.0.5`, и следующий релиз его унесёт.
64|
65|На живом Hub пользователя база создана версией `2.1.12`. Если совместимости нет, это проявится в момент `update-control`.
66|
67|Проверить: открытие существующей `control.db` сборкой из текущего `main`, `PRAGMA user_version` без повторного применения миграций, чтение агентов, identities, links, provisioning и аудита, `backup-create` и `backup-restore` на этой же базе, и то же самое на `linux-arm64` self-contained trimmed single-file.
68|
69|**Добавить постоянный тест** на открытие базы предыдущей схемы — эталонная `control.db` в репозитории либо скрипт её воспроизведения. Без него следующее обновление SQLite повторит эту историю.
70|
71|Если хоть одна проверка не проходит — откатить `#23` отдельным PR и написать об этом прямо.
72|
73|---
74|
75|## Часть 3 — дисциплина релизов
76|
77|Тег `v0.1.0-alpha.8` был сдвинут: сначала указывал на `80b4797`, теперь на `ad180e9`. Так делать нельзя, и теперь особенно: релиз содержит `server-monitor-manager-manifest.json` и подпись `manifest.sig`, которые описывают конкретное содержимое. Если тег двигается, подпись перестаёт что-либо доказывать, а у того, кто сверялся с прежним тегом, проверка не сойдётся.
78|
79|1. Зафиксировать правило в `docs/installer-contract.md`: **опубликованный тег неизменяем**; ошибка в сборке исправляется выпуском следующей версии, а не переписыванием тега.
80|2. Следующий релиз выпускать как `v0.1.0-alpha.9` после частей 1 и 2.
81|3. Убрать из `smm-setup.sh` (ассет релиза) временный обход бага `validate_control_url` — дефект исправлен в #24, обход помечен комментарием в коде.
82|4. Довести или закрыть PR #31.
83|
84|---
85|
86|## Критерий приёмки
87|
88|- снимок Monitor совпадает с `installer-contract.md` §7 плюс два поля systemd; лишних полей нет;
89|- переименование любого поля роняет тест противоположной стороны — продемонстрировать намеренной поломкой во временном коммите со ссылкой на красный прогон;
90|- существующая база открывается новой сборкой, есть постоянный тест на предыдущую схему;
91|- правило неизменяемости тега записано в контракте;
92|- обход `validate_control_url` из `smm-setup.sh` удалён;
93|- Control suite прогнан на Linux, CI зелёный.
94|
95|## Отчёт
96|
97|Раздельно: локально, в CI со ссылками, не запускалось и почему.
98|

## Tracked diff

```diff
diff --git a/.github/workflows/linux-control-agent.yml b/.github/workflows/linux-control-agent.yml
index fb27dc6..94c19cc 100644
--- a/.github/workflows/linux-control-agent.yml
+++ b/.github/workflows/linux-control-agent.yml
@@ -26,9 +26,6 @@ jobs:
       - name: Build
         run: dotnet build ServerMonitorManager.slnx --configuration Release --no-restore
 
-      - name: Generate reference alpha.7 control database
-        run: bash tests/acceptance/generate_alpha7_db.sh tests/acceptance/alpha7.db
-
       - name: Test
         run: dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj --configuration Release --no-build
 
@@ -42,6 +39,17 @@ jobs:
           bash -n tests/acceptance/three-server-mesh.sh
           shellcheck --severity=error tests/acceptance/three-server-mesh.sh
 
+      - name: Verify Monitor snapshot contract
+        run: |
+          bash -n tests/acceptance/test_monitor_snapshot.sh
+          bash -n tests/acceptance/test_previous_schema_contract.sh
+          bash -n tests/acceptance/test_previous_schema_arm64.sh
+          shellcheck --severity=error tests/acceptance/test_monitor_snapshot.sh
+          shellcheck --severity=error tests/acceptance/test_previous_schema_contract.sh
+          shellcheck --severity=error tests/acceptance/test_previous_schema_arm64.sh
+          bash tests/acceptance/test_monitor_snapshot.sh
+          bash tests/acceptance/test_previous_schema_contract.sh
+
       - name: Verify standalone bootstrap
         run: |
           bash -n deploy/ochenstarik-server-monitor-manager.sh
@@ -50,14 +58,17 @@ jobs:
           bash -n tests/bootstrap/run-native-systemd-smoke.sh
           bash -n tests/bootstrap/run-systemd-container-smoke.sh
           bash -n tests/bootstrap/test-enrollment-token-argv.sh
+          bash -n tests/bootstrap/test-release-contract.sh
           shellcheck --severity=error deploy/ochenstarik-server-monitor-manager.sh
           shellcheck --severity=error deploy/ochenstarik-smm-policy-apply
           shellcheck --severity=error deploy/ochenstarik-smm-emergency
           shellcheck --severity=error tests/bootstrap/run-native-systemd-smoke.sh
           shellcheck --severity=error tests/bootstrap/run-systemd-container-smoke.sh
           shellcheck --severity=error tests/bootstrap/test-enrollment-token-argv.sh
+          shellcheck --severity=error tests/bootstrap/test-release-contract.sh
           bash tests/bootstrap/test-bootstrap-contract.sh
           bash tests/bootstrap/test-enrollment-token-argv.sh
+          bash tests/bootstrap/test-release-contract.sh
 
       - name: Publish agent amd64
         run: dotnet publish src/ServerMonitorManager.Agent/ServerMonitorManager.Agent.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:RestoreLockedMode=true
@@ -65,6 +76,22 @@ jobs:
       - name: Publish agent arm64
         run: dotnet publish src/ServerMonitorManager.Agent/ServerMonitorManager.Agent.csproj --configuration Release --runtime linux-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:RestoreLockedMode=true
 
+      - name: Publish control arm64 compatibility binary
+        run: dotnet publish src/ServerMonitorManager.Control/ServerMonitorManager.Control.csproj --configuration Release --runtime linux-arm64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:RestoreLockedMode=true -o compatibility/control-linux-arm64
+
+      - name: Set up arm64 emulation
+        uses: docker/setup-qemu-action@c7c53464625b32c7a7e944ae62b3e17d2b600130  # v3
+        with:
+          platforms: arm64
+
+      - name: Verify previous schema with arm64 published Control
+        run: |
+          docker run --rm --platform linux/arm64 \
+            --volume "$PWD:/workspace" --workdir /workspace \
+            ubuntu:24.04 \
+            bash tests/acceptance/test_previous_schema_arm64.sh \
+              compatibility/control-linux-arm64/ochenstarik-smm-control
+
       - name: Publish provisioning helper amd64
         run: dotnet publish src/ServerMonitorManager.Provisioning.Helper/ServerMonitorManager.Provisioning.Helper.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:RestoreLockedMode=true
 
diff --git a/.github/workflows/linux-release.yml b/.github/workflows/linux-release.yml
index 957e50d..1341fbe 100644
--- a/.github/workflows/linux-release.yml
+++ b/.github/workflows/linux-release.yml
@@ -23,38 +23,51 @@ jobs:
       - name: Validate bootstrap
         run: |
           bash -n deploy/ochenstarik-server-monitor-manager.sh
+          bash -n deploy/smm-setup.sh
           bash -n deploy/ochenstarik-smm-policy-apply
           bash -n deploy/ochenstarik-smm-emergency
           bash -n tests/bootstrap/run-native-systemd-smoke.sh
           bash -n tests/bootstrap/run-systemd-container-smoke.sh
           shellcheck --severity=error deploy/ochenstarik-server-monitor-manager.sh
+          shellcheck --severity=error deploy/smm-setup.sh
           shellcheck --severity=error deploy/ochenstarik-smm-policy-apply
           shellcheck --severity=error deploy/ochenstarik-smm-emergency
           shellcheck --severity=error tests/bootstrap/run-native-systemd-smoke.sh
           shellcheck --severity=error tests/bootstrap/run-systemd-container-smoke.sh
           bash tests/bootstrap/test-bootstrap-contract.sh
           bash tests/bootstrap/test-manifest-verification.sh
+          bash tests/bootstrap/test-release-contract.sh
 
       - name: Package bootstrap
         shell: bash
         run: |
           set -Eeuo pipefail
-          install -m 0755 deploy/ochenstarik-server-monitor-manager.sh ochenstarik-server-monitor-manager.sh
+          DIST_DIR=dist
+          install -d "$DIST_DIR"
+          install -m 0755 deploy/ochenstarik-server-monitor-manager.sh "$DIST_DIR/ochenstarik-server-monitor-manager.sh"
+          install -m 0755 deploy/smm-setup.sh "$DIST_DIR/smm-setup.sh"
           
           # Substitute PROGRAM_VERSION from tag
           if [[ "${GITHUB_REF}" == refs/tags/* ]]; then
-            sed -i "s/^PROGRAM_VERSION=.*$/PROGRAM_VERSION=\"${GITHUB_REF_NAME}\"/" ochenstarik-server-monitor-manager.sh
+            sed -i "s/^PROGRAM_VERSION=.*$/PROGRAM_VERSION=\"${GITHUB_REF_NAME}\"/" "$DIST_DIR/ochenstarik-server-monitor-manager.sh"
+            grep -Fq "readonly DEFAULT_RELEASE_TAG=\"${GITHUB_REF_NAME}\"" "$DIST_DIR/smm-setup.sh"
           fi
           
-          sha256sum ochenstarik-server-monitor-manager.sh > ochenstarik-server-monitor-manager.sh.sha256
+          (
+            cd "$DIST_DIR"
+            sha256sum ochenstarik-server-monitor-manager.sh > ochenstarik-server-monitor-manager.sh.sha256
+            sha256sum smm-setup.sh > smm-setup.sh.sha256
+          )
 
       - name: Upload bootstrap artifact
         uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a  # v7.0.1
         with:
           name: server-monitor-manager-bootstrap
           path: |
-            ochenstarik-server-monitor-manager.sh
-            ochenstarik-server-monitor-manager.sh.sha256
+            dist/ochenstarik-server-monitor-manager.sh
+            dist/ochenstarik-server-monitor-manager.sh.sha256
+            dist/smm-setup.sh
+            dist/smm-setup.sh.sha256
 
   publish-linux:
     runs-on: ubuntu-latest
@@ -233,6 +246,7 @@ jobs:
           
           # Calculate hashes
           BOOTSTRAP_SHA=$(sha256sum artifacts/server-monitor-manager-bootstrap/ochenstarik-server-monitor-manager.sh | awk '{print $1}')
+          SETUP_SHA=$(sha256sum artifacts/server-monitor-manager-bootstrap/smm-setup.sh | awk '{print $1}')
           LINUX_X64_SHA=$(sha256sum artifacts/server-monitor-manager-linux-x64/server-monitor-manager-linux-x64.tar.gz | awk '{print $1}')
           LINUX_ARM64_SHA=$(sha256sum artifacts/server-monitor-manager-linux-arm64/server-monitor-manager-linux-arm64.tar.gz | awk '{print $1}')
           MSIX_SHA=$(sha256sum artifacts/server-monitor-manager-win-x64/artifacts/windows-installer/ServerMonitorManager-win-x64.msix | awk '{print $1}')
@@ -244,6 +258,7 @@ jobs:
             --arg schema "smm-manifest/v2" \
             --arg version "$VERSION" \
             --arg bootstrap_sha256 "$BOOTSTRAP_SHA" \
+            --arg setup_sha256 "$SETUP_SHA" \
             --arg linux_x64_sha256 "$LINUX_X64_SHA" \
             --arg linux_arm64_sha256 "$LINUX_ARM64_SHA" \
             --arg msix_sha256 "$MSIX_SHA" \
@@ -264,6 +279,7 @@ jobs:
               },
               hashes: {
                 "ochenstarik-server-monitor-manager.sh": $bootstrap_sha256,
+                "smm-setup.sh": $setup_sha256,
                 "server-monitor-manager-linux-x64.tar.gz": $linux_x64_sha256,
                 "server-monitor-manager-linux-arm64.tar.gz": $linux_arm64_sha256,
                 "ServerMonitorManager-win-x64.msix": $msix_sha256,
diff --git a/README.md b/README.md
index 9e3aeb8..39ce4d5 100644
--- a/README.md
+++ b/README.md
@@ -40,7 +40,7 @@ The control plane and data plane are separated:
 - **Desktop:** packaged WinUI 3 client with DPAPI-protected operator certificate and SSH identity.
 - **Agent:** self-contained Linux binary for `amd64` and `arm64`; it only creates outbound mTLS sessions.
 
-See [architecture](docs/architecture.md), [security model](docs/security-model.md), [roadmap](docs/roadmap.md), [Linux bootstrap contract](docs/installer-contract.md), and the [Provisioning and Xray specification](docs/provisioning-vpn-requirements.md).
+See [architecture](docs/architecture.md), [security model](docs/security-model.md), [roadmap](docs/roadmap.md), [Linux bootstrap contract](docs/installer-contract.md), [release policy](docs/release-policy.md), and the [Provisioning and Xray specification](docs/provisioning-vpn-requirements.md).
 
 Work order is governed by [product horizons](docs/product-horizons.md): the roadmap records what is done, the horizons record what may be started. Planned subsystems are specified separately in [approval policies](docs/approval-policies.md) and the [KAgent integration](docs/integration-kagent.md). Both describe target behaviour and are not descriptions of the current alpha; nothing in them is implemented.
 
diff --git a/deploy/ochenstarik-server-monitor-manager.sh b/deploy/ochenstarik-server-monitor-manager.sh
index 0dc6203..f5652ed 100755
--- a/deploy/ochenstarik-server-monitor-manager.sh
+++ b/deploy/ochenstarik-server-monitor-manager.sh
@@ -1245,11 +1245,6 @@ install_monitor() {
 set -euo pipefail
 # ochenstarik-smm-metrics
 
-MESH_STATUS=""
-if [[ -x /usr/bin/wg && -f /etc/wireguard/smm0.conf ]]; then
-    MESH_STATUS=$(wg show smm0 2>/dev/null || true)
-fi
-
 echo "PROTOCOL=1"
 echo "HOSTNAME=$(hostname)"
 UPTIME=$(awk '{print int($1)}' /proc/uptime 2>/dev/null || echo "0")
@@ -1287,10 +1282,10 @@ echo "NETWORK_RX_BYTES=${NET_RX}"
 echo "NETWORK_TX_BYTES=${NET_TX}"
 KERNEL=$(uname -r 2>/dev/null || echo "unknown")
 echo "KERNEL=${KERNEL}"
-if [ -n "$MESH_STATUS" ]; then
-    echo "--- MESH STATUS ---"
-    echo "$MESH_STATUS"
-fi
+SYSTEMD_SSH=$(systemctl is-active ssh.service 2>/dev/null || true)
+echo "SYSTEMD_SSH=${SYSTEMD_SSH:-unknown}"
+SYSTEMD_WIREGUARD=$(systemctl is-active wg-quick@smm0.service 2>/dev/null || true)
+echo "SYSTEMD_WIREGUARD=${SYSTEMD_WIREGUARD:-unknown}"
 EOF
     chown root:root "$metrics_script"
     chmod 0755 "$metrics_script"
diff --git a/docs/installer-contract.md b/docs/installer-contract.md
index 88981a5..9a3526e 100644
--- a/docs/installer-contract.md
+++ b/docs/installer-contract.md
@@ -6,6 +6,8 @@ Bootstrap, helper, systemd units, JSON schemas и manifests являются к
 
 Bootstrap не скачивает и не запускает исходники других проектов. Production-установка использует закреплённый release/tag, проверяет signed compatibility manifest и SHA-256 каждого artifact. Mutable `main` не является источником production-установки.
 
+Published release tags and their assets are immutable. A published tag must never be moved, reused, deleted and recreated, or supplied with replacement assets under the same names; corrections must preserve the existing release and publish a new, higher version tag.
+
 Пока bootstrap не опубликован в release, документация не должна предлагать несуществующую команду его скачивания.
 
 ## 2. Поддерживаемые роли
@@ -104,7 +106,7 @@ CLI является non-interactive, кроме локального ввода
 
 ## 7. Forced command monitoring
 
-Monitoring key допускает только versioned metrics snapshot и read-only mesh status. Полный SSH-терминал использует отдельную пользовательскую identity.
+Monitoring key permits only the exact versioned metrics snapshot listed below; no additional mesh status or other output is allowed. Полный SSH-терминал использует отдельную пользовательскую identity.
 
 Минимальный snapshot:
 
@@ -125,6 +127,8 @@ DISK_INODES_FREE=...
 NETWORK_RX_BYTES=...
 NETWORK_TX_BYTES=...
 KERNEL=...
+SYSTEMD_SSH=active|inactive|failed|unknown
+SYSTEMD_WIREGUARD=active|inactive|failed|unknown
 ```
 
 ## 8. Обязательные проверки
diff --git a/tests/ServerMonitorManager.Control.Tests/SchemaCompatibilityTests.cs b/tests/ServerMonitorManager.Control.Tests/SchemaCompatibilityTests.cs
index 272aae0..e7ddeda 100644
--- a/tests/ServerMonitorManager.Control.Tests/SchemaCompatibilityTests.cs
+++ b/tests/ServerMonitorManager.Control.Tests/SchemaCompatibilityTests.cs
@@ -1,43 +1,137 @@
-using System;
-using System.IO;
-using System.Threading;
-using System.Threading.Tasks;
 using Microsoft.Data.Sqlite;
+using Microsoft.Extensions.Logging.Abstractions;
 using Microsoft.Extensions.Options;
 using Xunit;
 
 namespace ServerMonitorManager.Control.Tests;
 
-public sealed class SchemaCompatibilityTests
+public sealed class SchemaCompatibilityTests : IAsyncDisposable
 {
+    private readonly string _directory = Path.Combine(
+        Path.GetTempPath(), $"smm-schema-compatibility-{Guid.NewGuid():N}");
+
     [Fact]
-    public async Task CanOpenAlpha7DatabaseWithoutReapplyingMigrations()
+    public async Task PublishedAlpha7DatabaseRemainsReadableAndRestorable()
     {
-        // Try to find the alpha7.db file which should be generated by CI
-        var dbPath = Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "acceptance", "alpha7.db"));
+        var cancellationToken = TestContext.Current.CancellationToken;
+        Directory.CreateDirectory(_directory);
+        var fixturePath = Path.Combine(
+            FindRepositoryRoot(), "tests", "fixtures", "control-v0.1.0-alpha.7.db");
+        Assert.True(File.Exists(fixturePath), $"Published database fixture is missing: {fixturePath}");
+
+        var options = new ControlOptions
+        {
+            DatabasePath = Path.Combine(_directory, "control.db"),
+            CertificateAuthorityPath = Path.Combine(_directory, "control-ca.pfx"),
+            BackupDirectory = Path.Combine(_directory, "backups")
+        };
+        File.Copy(fixturePath, options.DatabasePath);
+        await File.WriteAllBytesAsync(
+            options.CertificateAuthorityPath, [1, 2, 3, 4], cancellationToken);
+
+        var schemaVersionBefore = await ReadUserVersionAsync(options.DatabasePath, cancellationToken);
+        Assert.Equal(8L, schemaVersionBefore);
+
+        var store = new ControlStore(Options.Create(options));
+        await store.InitializeAsync(cancellationToken);
+
+        var schemaVersionAfter = await ReadUserVersionAsync(options.DatabasePath, cancellationToken);
+        Assert.Equal(schemaVersionBefore, schemaVersionAfter);
+
+        var agents = await store.ListAgentsAsync(cancellationToken);
+        Assert.Equal(["source-node", "target-node"], agents.Select(agent => agent.NodeId));
+        Assert.Equal("0.1.0-alpha.7", agents[0].AgentVersion);
 
-        if (!File.Exists(dbPath))
+        Assert.Equal(
+            new ControlIdentity("source-node", "Agent"),
+            await store.ResolveIdentityAsync("AGENT-THUMBPRINT", cancellationToken));
+        Assert.Equal(
+            new ControlIdentity("operator-device", "Operator"),
+            await store.ResolveIdentityAsync("DEVICE-THUMBPRINT", cancellationToken));
+        Assert.Equal(
+            new ControlIdentity("automation-one", "Automation", "source-node"),
+            await store.ResolveIdentityAsync("AUTOMATION-THUMBPRINT", cancellationToken));
+
+        var link = Assert.Single(await store.ListLinksAsync(cancellationToken));
+        Assert.Equal("source-node", link.SourceNodeId);
+        Assert.Equal("target-node", link.TargetNodeId);
+        Assert.Equal("Active", link.ActualState);
+
+        var job = await store.GetProvisioningJobAsync(
+            "22222222222222222222222222222222", cancellationToken);
+        Assert.NotNull(job);
+        Assert.Equal("source-node", job.NodeId);
+        Assert.Equal("Completed", job.State);
+        var provisioningEvent = Assert.Single((await store.ListProvisioningEventsAsync(
+            job.Id, 100, cancellationToken))!);
+        Assert.Equal("job.completed", provisioningEvent.EventType);
+
+        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
         {
-            // Skip the test locally if the file hasn't been generated
-            return;
+            await connection.OpenAsync(cancellationToken);
+            var audit = connection.CreateCommand();
+            audit.CommandText = "SELECT actor, action, subject, details_json FROM audit;";
+            await using var reader = await audit.ExecuteReaderAsync(cancellationToken);
+            Assert.True(await reader.ReadAsync(cancellationToken));
+            Assert.Equal("operator-device", reader.GetString(0));
+            Assert.Equal("fixture.created", reader.GetString(1));
+            Assert.Equal("source-node", reader.GetString(2));
+            Assert.Contains("v0.1.0-alpha.7", reader.GetString(3), StringComparison.Ordinal);
+            Assert.False(await reader.ReadAsync(cancellationToken));
         }
 
-        // 1. Verify PRAGMA user_version is readable and hasn't changed abruptly
-        await using var connection = new SqliteConnection($"Data Source={dbPath}");
-        await connection.OpenAsync();
-        var command = connection.CreateCommand();
-        command.CommandText = "PRAGMA user_version;";
-        var userVersion = Convert.ToInt64(await command.ExecuteScalarAsync());
+        var backups = new ControlBackupService(
+            store, Options.Create(options), NullLogger<ControlBackupService>.Instance);
+        var backupPath = await backups.CreateAsync(DateTimeOffset.UtcNow, cancellationToken);
+        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
+        {
+            await connection.OpenAsync(cancellationToken);
+            var mutate = connection.CreateCommand();
+            mutate.CommandText = "UPDATE agents SET name = 'mutated';";
+            await mutate.ExecuteNonQueryAsync(cancellationToken);
+        }
+        await File.WriteAllBytesAsync(options.CertificateAuthorityPath, [9, 9], cancellationToken);
 
-        Assert.True(userVersion > 0, "Database user_version should be greater than 0");
+        SqliteConnection.ClearAllPools();
+        await backups.RestoreAsync(backupPath, cancellationToken);
 
-        // 2. Open it with the current ControlStore to see if data reads correctly and it doesn't crash
-        var store = new ControlStore(Options.Create(new ControlOptions { DatabasePath = dbPath }));
-        await store.InitializeAsync(CancellationToken.None);
+        var restoredStore = new ControlStore(Options.Create(options));
+        await restoredStore.InitializeAsync(cancellationToken);
+        Assert.Equal(schemaVersionBefore, await ReadUserVersionAsync(options.DatabasePath, cancellationToken));
+        Assert.Equal("Source Node", (await restoredStore.ListAgentsAsync(cancellationToken))[0].Name);
+        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(
+            options.CertificateAuthorityPath, cancellationToken));
+    }
 
-        // 3. Test that we can read from it
-        var links = await store.ListEffectiveLinksForNodeAsync("nonexistent", CancellationToken.None);
-        Assert.Empty(links);
+    public ValueTask DisposeAsync()
+    {
+        SqliteConnection.ClearAllPools();
+        if (Directory.Exists(_directory))
+        {
+            Directory.Delete(_directory, recursive: true);
+        }
+        return ValueTask.CompletedTask;
+    }
 
+    private static async Task<long> ReadUserVersionAsync(
+        string databasePath,
+        CancellationToken cancellationToken)
+    {
+        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
+        await connection.OpenAsync(cancellationToken);
+        var command = connection.CreateCommand();
+        command.CommandText = "PRAGMA user_version;";
+        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
+    }
+
+    private static string FindRepositoryRoot()
+    {
+        var directory = new DirectoryInfo(AppContext.BaseDirectory);
+        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitorManager.slnx")))
+        {
+            directory = directory.Parent;
+        }
+        return directory?.FullName
+               ?? throw new DirectoryNotFoundException("Repository root was not found.");
     }
 }
diff --git a/tests/acceptance/generate_alpha7_db.sh b/tests/acceptance/generate_alpha7_db.sh
deleted file mode 100644
index 3aef7c3..0000000
--- a/tests/acceptance/generate_alpha7_db.sh
+++ /dev/null
@@ -1,41 +0,0 @@
-#!/bin/bash
-set -euo pipefail
-
-OUTPUT_DB=$(realpath "${1:-alpha7.db}")
-
-if [ -f "$OUTPUT_DB" ]; then
-    echo "Database $OUTPUT_DB already exists."
-    exit 0
-fi
-
-echo "Generating reference database from v0.1.0-alpha.7 to $OUTPUT_DB"
-WORK_DIR=$(mktemp -d)
-trap 'rm -rf "$WORK_DIR"' EXIT
-
-git clone --depth 1 -b v0.1.0-alpha.7 https://github.com/ochenstarik-ui/server-monitor-manager.git "$WORK_DIR/repo"
-
-cat << 'EOF' > "$WORK_DIR/repo/tests/ServerMonitorManager.Control.Tests/GenerateDbTest.cs"
-using System.IO;
-using System.Threading;
-using System.Threading.Tasks;
-using Xunit;
-using ServerMonitorManager.Control;
-using Microsoft.Extensions.Options;
-
-namespace ServerMonitorManager.Control.Tests;
-
-public class GenerateDbTest
-{
-    [Fact]
-    public async Task GenerateReferenceDatabase()
-    {
-        var dbPath = System.Environment.GetEnvironmentVariable("OUTPUT_DB_PATH");
-        var store = new ControlStore(Options.Create(new ControlOptions { DatabasePath = dbPath }));
-        await store.InitializeAsync(CancellationToken.None);
-    }
-}
-EOF
-
-export OUTPUT_DB_PATH="$OUTPUT_DB"
-cd "$WORK_DIR/repo"
-dotnet test tests/ServerMonitorManager.Control.Tests --filter "GenerateReferenceDatabase"
diff --git a/tests/acceptance/test_monitor_snapshot.sh b/tests/acceptance/test_monitor_snapshot.sh
index 79855ce..4901c5f 100644
--- a/tests/acceptance/test_monitor_snapshot.sh
+++ b/tests/acceptance/test_monitor_snapshot.sh
@@ -1,58 +1,54 @@
 #!/usr/bin/env bash
-set -euo pipefail
+set -Eeuo pipefail
+IFS=$'\n\t'
 
-# test_monitor_snapshot.sh
-# Acceptance test for Monitor role installation and metrics format
+root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
+bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
+contract="$root/tests/contracts/monitor-snapshot-v1.txt"
+installer_contract="$root/docs/installer-contract.md"
 
-cd "$(dirname "$0")/../.."
-BOOTSTRAP="./deploy/ochenstarik-server-monitor-manager.sh"
-DUMMY_KEY="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDummyTestKeyForAcceptanceTest dummy@test"
-
-echo "=== Testing install-monitor ==="
-sudo "$BOOTSTRAP" install-monitor "$DUMMY_KEY"
-
-# 1. Verify user exists and has nologin
-getent passwd ochenstarik-monitor | grep -q "/usr/sbin/nologin" || {
-    echo "ERROR: User ochenstarik-monitor does not exist or does not have nologin"
-    exit 1
-}
-
-# 2. Verify authorized_keys
-AUTH_KEYS="/var/lib/ochenstarik-monitor/.ssh/authorized_keys"
-if ! sudo test -f "$AUTH_KEYS"; then
-    echo "ERROR: authorized_keys not found"
+grep -Fq 'Monitoring key permits only the exact versioned metrics snapshot listed below; no additional mesh status or other output is allowed.' "$installer_contract"
+if grep -Fq 'read-only mesh status' "$installer_contract"; then
+    printf '%s\n' 'installer contract still permits mesh status outside the closed snapshot' >&2
     exit 1
 fi
 
-sudo grep -q "command=\"/usr/local/libexec/ochenstarik-smm-metrics\",restrict" "$AUTH_KEYS" || {
-    echo "ERROR: Forced command not found in authorized_keys"
-    exit 1
+extract_metrics_script() {
+    awk '
+        /cat >"\$metrics_script" <<'"'"'EOF'"'"'/ { emitting = 1; next }
+        emitting && $0 == "EOF" { exit }
+        emitting { print }
+    ' "$bootstrap"
 }
 
-# 3. Verify metrics script output format
-echo "=== Testing metrics output ==="
-METRICS_OUT=$(sudo /usr/local/libexec/ochenstarik-smm-metrics)
-echo "$METRICS_OUT"
+fixture="$(mktemp -d -t smm-monitor-snapshot.XXXXXXXX)"
+trap 'rm -rf -- "$fixture"' EXIT
+metrics_script="$fixture/ochenstarik-smm-metrics"
+extract_metrics_script >"$metrics_script"
+chmod 0755 "$metrics_script"
 
-for FIELD in PROTOCOL HOSTNAME UPTIME_SECONDS LOAD1 CPU_COUNT MEM_TOTAL_KB MEM_AVAILABLE_KB SWAP_TOTAL_KB SWAP_FREE_KB DISK_TOTAL_KB DISK_AVAILABLE_KB DISK_INODES_TOTAL DISK_INODES_FREE NETWORK_RX_BYTES NETWORK_TX_BYTES KERNEL; do
-    if ! echo "$METRICS_OUT" | grep -q "^${FIELD}="; then
-        echo "ERROR: Missing field ${FIELD} in metrics output"
-        exit 1
-    fi
-done
+[[ -s "$metrics_script" ]] || {
+    printf '%s\n' 'monitor metrics script was not found in bootstrap' >&2
+    exit 1
+}
 
-# 4. Verify uninstall
-echo "=== Testing uninstall-monitor ==="
-sudo "$BOOTSTRAP" uninstall-monitor
+metrics_out="$(bash "$metrics_script")"
+printf '%s\n' "$metrics_out"
 
-if getent passwd ochenstarik-monitor >/dev/null 2>&1; then
-    echo "ERROR: User ochenstarik-monitor was not removed"
+if grep -Ev '^[A-Z][A-Z0-9_]*=.*$' <<<"$metrics_out"; then
+    printf '%s\n' 'monitor snapshot contains non-contract output' >&2
     exit 1
 fi
 
-if sudo test -f "$AUTH_KEYS" || sudo test -f "/usr/local/libexec/ochenstarik-smm-metrics"; then
-    echo "ERROR: Leftover files found after uninstall"
+expected_keys="$(cut -d= -f1 "$contract" | LC_ALL=C sort)"
+actual_keys="$(cut -d= -f1 <<<"$metrics_out" | LC_ALL=C sort)"
+if [[ "$actual_keys" != "$expected_keys" ]]; then
+    printf '%s\n' 'monitor snapshot field closure differs from canonical contract' >&2
+    diff -u <(printf '%s\n' "$expected_keys") <(printf '%s\n' "$actual_keys") >&2 || true
     exit 1
 fi
 
-echo "=== PASS: Monitor role acceptance test ==="
+[[ "$(grep -c '^PROTOCOL=1$' <<<"$metrics_out")" -eq 1 ]]
+[[ "$(wc -l <"$contract")" -eq "$(wc -l <<<"$metrics_out")" ]]
+
+printf '%s\n' 'MONITOR_SNAPSHOT_CONTRACT=PASS'
```

## Untracked file: `deploy/smm-setup.sh`

```
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly PROGRAM_NAME="smm-setup"
readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.9"
readonly DEFAULT_REPOSITORY="ochenstarik-ui/server-monitor-manager"
readonly INNER_ASSET="ochenstarik-server-monitor-manager.sh"

RELEASE_TAG="${SMM_TAG:-$DEFAULT_RELEASE_TAG}"
REPOSITORY="${SMM_REPOSITORY:-$DEFAULT_REPOSITORY}"
CACHE_DIR="${SMM_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/server-monitor-manager}"

usage() {
    cat <<'USAGE'
Usage:
  smm-setup.sh [--tag TAG] [--repository OWNER/REPO] COMMAND [ARG...]

Commands are passed to the verified ochenstarik-server-monitor-manager.sh
asset from the selected immutable GitHub release. Common commands:
  install-agent | install-control | uninstall-agent | uninstall-control
  backup-create | backup-restore | version

Environment overrides:
  SMM_TAG         Release tag (default: v0.1.0-alpha.9)
  SMM_REPOSITORY  GitHub repository (default: ochenstarik-ui/server-monitor-manager)
  SMM_CACHE_DIR   Verified-download cache directory
USAGE
}

die() {
    printf '%s: %s\n' "$PROGRAM_NAME" "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command is unavailable: $1"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag)
            [[ $# -ge 2 ]] || die '--tag requires a value'
            RELEASE_TAG="$2"
            shift 2
            ;;
        --repository)
            [[ $# -ge 2 ]] || die '--repository requires a value'
            REPOSITORY="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --)
            shift
            break
            ;;
        -*)
            die "unknown option: $1"
            ;;
        *)
            break
            ;;
    esac
done

[[ $# -gt 0 ]] || {
    usage >&2
    exit 2
}
[[ "$RELEASE_TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] \
    || die "invalid release tag: $RELEASE_TAG"
[[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] \
    || die "invalid repository: $REPOSITORY"

require_command curl
require_command sha256sum
require_command mktemp

release_base="https://github.com/$REPOSITORY/releases/download/$RELEASE_TAG"
cache_release="$CACHE_DIR/$REPOSITORY/$RELEASE_TAG"
cached_script="$cache_release/$INNER_ASSET"
cached_checksum="$cache_release/$INNER_ASSET.sha256"
mkdir -p "$cache_release"

temporary_directory="$(mktemp -d -t smm-setup.XXXXXXXX)"
trap 'rm -rf -- "$temporary_directory"' EXIT

curl -fsSL "$release_base/$INNER_ASSET" -o "$temporary_directory/$INNER_ASSET"
curl -fsSL "$release_base/$INNER_ASSET.sha256" -o "$temporary_directory/$INNER_ASSET.sha256"

(
    cd "$temporary_directory"
    sha256sum -c "$INNER_ASSET.sha256"
) || die "checksum verification failed for $RELEASE_TAG/$INNER_ASSET"

install -m 0755 "$temporary_directory/$INNER_ASSET" "$cached_script"
install -m 0644 "$temporary_directory/$INNER_ASSET.sha256" "$cached_checksum"

exec "$cached_script" "$@"

```

## Untracked file: `docs/release-policy.md`

```
# Release policy

Published tags and release assets are immutable.

A tag that has been published must never be moved, reused, deleted and recreated, or supplied with replacement assets under the same names. If a published build or installer is wrong, preserve the existing release and publish a new, higher version tag containing the correction.

Release artifacts are built from the commit named by the tag through the repository release workflows, including `.github/workflows/linux-release.yml` and `.github/workflows/windows-release.yml`. The tracked production source for the convenience installer is `deploy/smm-setup.sh`; the Linux release workflow copies that exact file to the release artifact set, records its SHA-256 in the signed manifest, and publishes its standalone checksum. The default release in that source must match the tag being produced.

For `v0.1.0-alpha.9`, this makes `smm-setup.sh`, `smm-setup.sh.sha256`, the bootstrap script, platform archives, SBOMs, and the signed manifest reproducible from the tagged tree. The installer fetches only same-tag assets and verifies the bootstrap checksum before execution. Corrections after publication require another tag; the `v0.1.0-alpha.9` tag and assets remain unchanged.

```

## Untracked file: `tests/ServerMonitorManager.Control.Tests/DesktopMonitorSnapshotContractTests.cs`

```
using System.Text.RegularExpressions;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class DesktopMonitorSnapshotContractTests
{
    [Fact]
    public void QueryParserFieldNamesMatchCanonicalSnapshot()
    {
        var root = FindRepositoryRoot();
        var canonicalKeys = ReadKeys(Path.Combine(root, "tests", "contracts", "monitor-snapshot-v1.txt"));
        var source = File.ReadAllText(Path.Combine(
            root, "src", "ServerMonitorManager.Desktop", "SshMonitorService.cs"));
        var queryStart = source.IndexOf("public async Task<ServerMetrics> QueryAsync(", StringComparison.Ordinal);
        var queryEnd = source.IndexOf(
            "public async Task<string> RunRestrictedCommandAsync(", queryStart, StringComparison.Ordinal);
        Assert.True(queryStart >= 0 && queryEnd > queryStart, "Desktop QueryAsync source was not found.");

        var querySource = source[queryStart..queryEnd];
        var parserKeys = Regex.Matches(querySource, "\"([A-Z][A-Z0-9_]*)\"")
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // PROTOCOL gates the wire format and KERNEL is diagnostic metadata; the current
        // ServerMetrics model intentionally does not project either value.
        var unprojectedKeys = new HashSet<string>(["PROTOCOL", "KERNEL"], StringComparer.Ordinal);
        Assert.Subset(canonicalKeys, unprojectedKeys);
        Assert.Equal(
            canonicalKeys.Except(unprojectedKeys).Order(StringComparer.Ordinal),
            parserKeys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void InstallerDocumentationListsCanonicalSnapshotFields()
    {
        var root = FindRepositoryRoot();
        var canonicalKeys = ReadKeys(Path.Combine(root, "tests", "contracts", "monitor-snapshot-v1.txt"));
        var documentation = File.ReadAllText(Path.Combine(root, "docs", "installer-contract.md"));
        var sectionStart = documentation.IndexOf("## 7. Forced command monitoring", StringComparison.Ordinal);
        var sectionEnd = documentation.IndexOf("## 8.", sectionStart, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0 && sectionEnd > sectionStart, "Installer contract section 7 was not found.");

        var documentedKeys = Regex.Matches(
                documentation[sectionStart..sectionEnd],
                "^([A-Z][A-Z0-9_]*)=",
                RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            canonicalKeys.Order(StringComparer.Ordinal),
            documentedKeys.Order(StringComparer.Ordinal));
    }

    private static HashSet<string> ReadKeys(string path)
        => File.ReadAllLines(path)
            .Select(line => line.Split('=', 2)[0])
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitorManager.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

```

## Untracked file: `tests/acceptance/test_previous_schema_arm64.sh`

```
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

[[ $# -eq 1 ]] || {
    printf 'usage: %s CONTROL_BINARY\n' "$0" >&2
    exit 2
}

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
binary="$(realpath "$1")"
fixture="$root/tests/fixtures/control-v0.1.0-alpha.7.db"
work="$(mktemp -d -t smm-arm64-schema-compatibility.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT

[[ -x "$binary" ]] || {
    printf 'published Control binary is not executable: %s\n' "$binary" >&2
    exit 1
}
cp "$fixture" "$work/control.db"
printf '%s' 'fixture-ca' >"$work/control-ca.pfx"
mkdir -p "$work/backups" "$work/bundle"

export Control__DatabasePath="$work/control.db"
export Control__CertificateAuthorityPath="$work/control-ca.pfx"
export Control__BackupDirectory="$work/backups"
export Control__HubHelperPath=/bin/true
export Control__PrivilegeEscalationPath=/usr/bin/true
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work/bundle"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

"$binary" backup-create
backup_paths=("$work"/backups/backup-*)
[[ ${#backup_paths[@]} -eq 1 && -d "${backup_paths[0]}" ]] || {
    printf '%s\n' 'arm64 published Control did not create exactly one backup' >&2
    exit 1
}

rm -f -- "$work/control.db" "$work/control.db-wal" "$work/control.db-shm" "$work/control-ca.pfx"
"$binary" backup-restore "${backup_paths[0]}"
[[ -s "$work/control.db" && -s "$work/control-ca.pfx" ]]
"$binary" backup-create

printf '%s\n' 'PREVIOUS_SCHEMA_ARM64=PASS'

```

## Untracked file: `tests/acceptance/test_previous_schema_contract.sh`

```
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
test_source="$root/tests/ServerMonitorManager.Control.Tests/SchemaCompatibilityTests.cs"
workflow="$root/.github/workflows/linux-control-agent.yml"
arm64_test="$root/tests/acceptance/test_previous_schema_arm64.sh"
fixture="$root/tests/fixtures/control-v0.1.0-alpha.7.db"

require_contains() {
    local text="$1" file="$2"
    grep -Fq "$text" "$file" || {
        printf 'required compatibility evidence is missing from %s: %s\n' "$file" "$text" >&2
        exit 1
    }
}

[[ -s "$fixture" ]] || {
    printf '%s\n' 'published previous-schema database fixture is missing' >&2
    exit 1
}
require_contains 'PublishedAlpha7DatabaseRemainsReadableAndRestorable' "$test_source"
require_contains 'Assert.Equal(schemaVersionBefore, schemaVersionAfter);' "$test_source"
require_contains 'ListAgentsAsync' "$test_source"
require_contains 'ResolveIdentityAsync' "$test_source"
require_contains 'ListLinksAsync' "$test_source"
require_contains 'GetProvisioningJobAsync' "$test_source"
require_contains 'FROM audit' "$test_source"
require_contains 'CreateAsync' "$test_source"
require_contains 'RestoreAsync' "$test_source"

if grep -Fq 'if (!File.Exists' "$test_source" || grep -Fq 'Generate reference alpha.7' "$workflow"; then
    printf '%s\n' 'previous-schema coverage is still skip- or generation-prone' >&2
    exit 1
fi

require_contains 'Publish control arm64 compatibility binary' "$workflow"
require_contains 'Verify previous schema with arm64 published Control' "$workflow"
require_contains 'export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1' "$arm64_test"

printf '%s\n' 'PREVIOUS_SCHEMA_CONTRACT=PASS'

```

## Untracked file: `tests/bootstrap/test-release-contract.sh`

```
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
setup="$root/deploy/smm-setup.sh"
workflow="$root/.github/workflows/linux-release.yml"
policy="$root/docs/release-policy.md"
installer_contract="$root/docs/installer-contract.md"

[[ -s "$setup" ]] || {
    printf '%s\n' 'tracked production smm-setup.sh source is missing' >&2
    exit 1
}
bash -n "$setup"
grep -Fq 'readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.9"' "$setup"
if grep -Fq 'validate_control_url' "$setup" || grep -Fq '${CONTROL_URL%/}/control' "$setup"; then
    printf '%s\n' 'temporary control URL workaround must not be present in smm-setup.sh' >&2
    exit 1
fi

grep -Fq 'install -m 0755 deploy/smm-setup.sh "$DIST_DIR/smm-setup.sh"' "$workflow"
grep -Fq 'smm-setup.sh.sha256' "$workflow"
grep -Fq 'dist/smm-setup.sh' "$workflow"
grep -Fq 'Published tags and release assets are immutable.' "$policy"
grep -Fq 'publish a new, higher version tag' "$policy"
grep -Fq 'Published release tags and their assets are immutable.' "$installer_contract"
grep -Fq 'publish a new, higher version tag' "$installer_contract"

work="$(mktemp -d -t smm-setup-contract.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/home"
cat >"$work/inner.sh" <<'INNER'
#!/usr/bin/env bash
printf 'INNER_COMMAND=%s\n' "$1"
INNER
chmod +x "$work/inner.sh"
inner_hash="$(sha256sum "$work/inner.sh" | cut -d' ' -f1)"
cat >"$work/bin/curl" <<EOF_CURL
#!/usr/bin/env bash
set -Eeuo pipefail
url=""
out=""
while [[ \$# -gt 0 ]]; do
    case "\$1" in
        -o) out="\$2"; shift 2 ;;
        -*) shift ;;
        *) url="\$1"; shift ;;
    esac
done
printf '%s\n' "\$url" >>'$work/urls'
case "\$url" in
    */ochenstarik-server-monitor-manager.sh)
        cp '$work/inner.sh' "\$out"
        ;;
    */ochenstarik-server-monitor-manager.sh.sha256)
        printf '%s  %s\n' '$inner_hash' 'ochenstarik-server-monitor-manager.sh' >"\$out"
        ;;
    *)
        printf 'unexpected URL: %s\n' "\$url" >&2
        exit 1
        ;;
esac
EOF_CURL
chmod +x "$work/bin/curl"

HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" version >"$work/output"
grep -Fq 'INNER_COMMAND=version' "$work/output"
grep -Fq '/releases/download/v0.1.0-alpha.9/ochenstarik-server-monitor-manager.sh' "$work/urls"
grep -Fq '/releases/download/v0.1.0-alpha.9/ochenstarik-server-monitor-manager.sh.sha256' "$work/urls"

printf '%s\n' 'RELEASE_CONTRACT=PASS'

```

## Untracked file: `tests/contracts/monitor-snapshot-v1.txt`

```
PROTOCOL=1
HOSTNAME=contract-host
UPTIME_SECONDS=12345
LOAD1=0.42
CPU_COUNT=4
MEM_TOTAL_KB=8192
MEM_AVAILABLE_KB=4096
SWAP_TOTAL_KB=2048
SWAP_FREE_KB=1024
DISK_TOTAL_KB=1048576
DISK_AVAILABLE_KB=524288
DISK_INODES_TOTAL=65536
DISK_INODES_FREE=32768
NETWORK_RX_BYTES=123456
NETWORK_TX_BYTES=654321
KERNEL=6.8.0-contract
SYSTEMD_SSH=active
SYSTEMD_WIREGUARD=inactive

```

## Untracked file: `tests/fixtures/README.md`

```
# Published Control database fixture

`control-v0.1.0-alpha.7.db` was created by the ControlStore from the immutable source tag `v0.1.0-alpha.7` (`d645812d29d077e9a4dee1596ef09a70dc138090`). That project pins `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12. The fixture contains stable rows for agents, Agent/Operator/Automation identities, a Link, a provisioning job and event, and audit data.

Fixture properties:

- `PRAGMA user_version`: 8
- `PRAGMA integrity_check`: `ok`
- SHA-256: `15bf788dd5789a55bd54a4a548d339b4e29e54e1c75311b21eec079e0ef2faa2`

The compatibility test always copies this file before opening it, so the committed previous-release database is never migrated or mutated in place.

```

## Untracked file: `tests/fixtures/control-v0.1.0-alpha.7.db-wal`

```

```

## Binary fixture evidence

- tests/fixtures/control-v0.1.0-alpha.7.db
- SHA-256: 15bf788dd5789a55bd54a4a548d339b4e29e54e1c75311b21eec079e0ef2faa2
- PRAGMA user_version: 8
- PRAGMA integrity_check: ok
