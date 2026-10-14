# Managed fixtures for native host tests

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

`protect-function-key` prepares a function credential using the production
`FunctionCredentialProtector` and Redis Data Protection registration. This verb
requires `CONDUIT_NATIVE_FUNCTION_PLAINTEXT_KEY` and
`CONDUIT_NATIVE_FUNCTION_REDIS_CONNECTION_STRING`; it does not require a database
connection. It uses the hosts' `Conduit` application discriminator and
`Conduit-DataProtection-Keys` Redis key ring, and writes a single `enc:v1:`
ciphertext line. Capture this output privately. The Gateway parity script supplies
these inputs only in the helper child's environment, captures both output streams
without logging them, and bounds the process lifetime and cleanup.

The real Redis credential tests retain the production application discriminator
and protector purpose, while overriding the XML repository only in the test to a
unique owned key. They verify decryption from a fresh provider and failures for
corruption, wrong purpose, and wrong application. Cleanup deletes only that owned
key; it never removes the shared production key ring. Credential preparation does
not establish support for a native function execution workflow.
