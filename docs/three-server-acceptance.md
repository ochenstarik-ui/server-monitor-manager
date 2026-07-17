# Three-server acceptance test

The acceptance script verifies one public Hub, one source Node used by an AI agent, and two independent destinations (for example a home server and a second remote server). It creates a temporary mTLS Operator identity, exercises two directional Links, disables only one Link, waits for server-side TTL expiration, and creates a verified Control backup.

Run it from a trusted Linux administration machine that can SSH to the Hub and source Node:

```bash
export HUB_SSH_HOST=203.0.113.10
export HUB_SSH_USER=admin
export SOURCE_SSH_HOST=198.51.100.20
export SOURCE_SSH_USER=admin
export SSH_IDENTITY_FILE="$HOME/.ssh/id_ed25519"
export SOURCE_NODE_ID=ai-agent
export HOME_NODE_ID=home
export SECOND_NODE_ID=second
export HOME_WG_IP=10.77.0.3
export SECOND_WG_IP=10.77.0.4
export TARGET_PORT=22

bash tests/acceptance/three-server-mesh.sh
```

Set `SMM_ACCEPT_REBOOT=1` to reboot the Hub and source Node and verify that the first Link remains usable while the disabled Link remains blocked. The reboot option is deliberately opt-in because it interrupts active sessions.

Prerequisites:

- all three Agent identities are enrolled and visible in Control Hub;
- `curl`, `jq`, `openssl`, `ssh`, `nc`, and GNU `base64` are installed on the administration machine;
- the SSH account can run the installer lifecycle command and `systemctl` through sudo;
- `TARGET_PORT` listens on both destination Nodes;
- `HOME_WG_IP` and `SECOND_WG_IP` are their `smm0` addresses.

Success ends with `THREE_SERVER_ACCEPTANCE=PASS`. The script keeps the home Link enabled for continued testing, leaves the second Link disabled, and never exports the Hub CA private key.
