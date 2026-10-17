# Database pool ownership

Admin owns one singleton `NpgsqlDataSource` for its regular application database
work. Entity Framework contexts, schema probes, typed runtime stores, coordinated
pool warmup, and the raw `database_health` check borrow connections from that data
source. The health check therefore exercises the same pool that application work
uses and startup warmup prepares.

`AddCoreServices` resolves the connection string with the `AdminAPI` service
profile before constructing this data source. PostgreSQL URL defaults are a
minimum of 5 connections and a maximum of 75 for Admin. Gateway retains its
separate `CoreAPI` profile, with defaults of 10 and 150. Existing explicit caller
pooling options retain their behavior; the raw Admin health factory does not
parse `DATABASE_URL` again or replace those settings with generic defaults.

The host owns and disposes the shared data source. `DatabaseConnectionFactory`
borrows it through `FromDataSource`; each returned connection belongs to the caller
and must be disposed after use. Disposing a returned connection releases its lease
without closing the host pool. `DatabaseHealthCheck` disposes its connection after
executing `SELECT 1`. Host shutdown closes the data source after its consumers
finish.

The asynchronous factory retains ownership until opening succeeds. A failed or
canceled open disposes the allocated connection asynchronously before returning
the error; cleanup failures are logged without replacing the original failure.
An already canceled caller token prevents allocation. Caller cancellation
propagates as `OperationCanceledException`, while other open failures retain their
original exception inside the factory's `InvalidOperationException`.

The raw health check asynchronously disposes its command and successful connection
lease on every path. It propagates caller cancellation instead of reporting it as
an unhealthy database. Other connection, query, and cleanup failures produce an
unhealthy result; cleanup failures cannot replace an earlier query failure or
caller cancellation.

The shared `AddDatabaseConnectionFactory` registration and public two-argument
`DatabaseConnectionFactory` constructor retain the existing string-based behavior
for callers that have not selected a host-owned source. Generic PostgreSQL URL
pool defaults remain a minimum of 1 and maximum of 100. Admin overrides only its
`IDatabaseConnectionFactory` registration; it does not change those generic callers.
The [PostgreSQL configuration contract](postgresql-connection-configuration.md)
defines accepted URI/key-value forms and how explicit settings override defaults.

Advisory locks continue to use their independently owned lock pool. Long-lived
lock sessions must not compete with routine database queries or health probes for
the application pool's connections. This change does not replace the lock
provider's pool or alter lock ownership lifetimes.

## Persistence test fixtures

Schema-isolated parity fixtures own their `NpgsqlDataSource` instances explicitly.
EF receives its fixture's source through `UseNpgsql(dataSource)`; disposing an EF
context returns its connection lease without disposing that source. Mixed EF and
typed tests can own separate sources with the same schema connection string, and
must dispose both after their consumers and leases finish. Shared management
connections are borrowed separately; fixture cleanup must never clear all pools.

The async-task fixture uses unpooled management connections and gives each owned
schema drop its own cancellation deadline. A failed first drop cannot skip the
second, and cleanup failures retain an earlier work failure. Billing, discovery,
and async-task lifetime regressions observe exact backend PIDs from inside the
still-running test process, then require those PIDs and owned schemas to disappear
while an unrelated pooled sentinel connection remains usable. This distinguishes
source disposal from connections merely closing when a test process exits.
