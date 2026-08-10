# Published Control database fixture

`control-v0.1.0-alpha.7.db` was created by the ControlStore from the immutable source tag `v0.1.0-alpha.7` (`d645812d29d077e9a4dee1596ef09a70dc138090`). That project pins `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12. The fixture contains stable rows for agents, Agent/Operator/Automation identities, a Link, a provisioning job and event, and audit data.

Fixture properties:

- `PRAGMA user_version`: 8
- `PRAGMA integrity_check`: `ok`
- SHA-256: `15bf788dd5789a55bd54a4a548d339b4e29e54e1c75311b21eec079e0ef2faa2`

The compatibility test always copies this file before opening it, so the committed previous-release database is never migrated or mutated in place.
