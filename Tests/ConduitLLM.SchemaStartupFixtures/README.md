# Native schema startup fixtures

This managed test utility creates a separate PostgreSQL database for native host
startup tests. It stays outside service images and the native client probe. Set
`CONDUIT_NATIVE_STARTUP_MANAGEMENT_CONNECTION_STRING` to a PostgreSQL connection
string whose role can create and remove databases.

`create missing` creates an empty database from `template0`. `create stale` applies
the release's actual penultimate EF migration, leaving the latest migration
unapplied. Both commands write one credential-bearing JSON object to standard
output; capture it privately rather than forwarding it into CI logs. It contains
the generated database name, connection string, applied version, and expected
release version. Native hosts must use that database, and only the standalone
release migrator upgrades it.

`drop <generated-name>` validates the generated name and the database ownership
comment before removing it. It cannot remove the management database or an
unmarked database; repeated cleanup after removal is safe. Call it after stopping
owned host processes, with an independent cleanup deadline. Creation failures
attempt bounded cleanup and identify the generated name in diagnostics if the
database creation outcome is uncertain.

The fixture requires the default `public` search path and rejects PostgreSQL
session `Options`, so application tables cannot resolve outside the owned
database's intended schema. Native readiness and message-runtime lifecycle are
verified separately by the published-process gate.
