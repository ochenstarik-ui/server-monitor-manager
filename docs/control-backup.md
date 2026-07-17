# Control Hub maintenance and recovery

Control Hub runs maintenance every 15 minutes by default. It removes expired enrollment tokens, old replay records, metrics older than seven days, and audit records older than 90 days. The intervals and retention windows are configured in the `Control` section of `appsettings.json` or with `Control__...` environment variables.

## Automatic backups

Every 24 hours Control creates a consistent SQLite online backup together with the Control CA. The default directory is:

```text
/var/lib/ochenstarik-server-monitor-manager/backups
```

Seven backups are retained by default. Every backup directory contains:

- `control.db` created with the SQLite backup API;
- `control-ca.pfx` with owner-only permissions;
- `manifest.json` with SHA-256 hashes and format version.

Create a backup immediately without interrupting the running service:

```bash
sudo systemd-run --wait --pipe --quiet --collect \
  --uid=ochenstarik-smm-control \
  --gid=ochenstarik-smm-control \
  -p EnvironmentFile=/etc/ochenstarik-server-monitor-manager/control.env \
  /usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control backup-create
```

## Restore

Restore is intentionally offline. Stop Control, invoke the binary as root with the same environment file, and start Control again:

```bash
BACKUP=/var/lib/ochenstarik-server-monitor-manager/backups/backup-YYYYMMDDTHHMMSSZ-id

sudo systemctl stop ochenstarik-smm-control.service
sudo sh -c 'set -a; . /etc/ochenstarik-server-monitor-manager/control.env; set +a; exec /usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control backup-restore "$1"' sh "$BACKUP"
sudo systemctl start ochenstarik-smm-control.service
curl --fail --silent https://127.0.0.1:7443/healthz
```

Restore validates both SHA-256 hashes and runs `PRAGMA integrity_check` before replacing live state. Existing files are copied to a root-only `pre-restore-*` directory before replacement. Keep that directory until server inventory, certificates, Links, and Agent heartbeats have been verified.
