# Global settings cache recovery

Admin and Gateway register one process-local `GlobalSettingsCacheService` as both
the settings reader and its hosted service. Startup attempts one complete database
load. If the database is unavailable or its schema has not been migrated yet,
startup continues with the existing cache or built-in defaults while background
recovery retries the load. A database failure does not hold host startup in an
unbounded retry loop.

Recovery waits 1 second before its first retry, then 2, 4, 8, 16, and at most 30
seconds between failed attempts. It stops after a successful complete load,
including a successful explicit reload, or when the host stops. A recovered
process observes persisted settings through the same cache instance without an
operator reload or restart. Host startup cancellation propagates to the caller
and does not schedule background recovery.

Redis reload subscription failures are logged separately and do not prevent the
initial database load or its recovery. Redis broadcasts and explicit reloads keep
their existing routing behavior; a process without Redis reloads itself. Recovery
only repairs an unsuccessful initial database load. It does not retry a failed
Redis subscription or continuously poll a cache whose initial load succeeded.

Initial loading, recovery, full reloads, and per-key invalidations share one lock.
Readers continue to see the previous complete snapshot while database work runs.
A successful full load publishes the new dictionary and load timestamp together.
Failed or canceled loading retains the previous snapshot; a failed per-key refresh
also retains its previous value. Deleting a setting removes it after the database
successfully confirms that it is absent.

Shutdown cancels retry delays and pending repository calls, awaits recovery, and
drains any active load or invalidation before clearing the local snapshot. A
repository that returns after cancellation cannot publish its late result. New
reloads and invalidations after shutdown are ignored. Redis subscription cleanup
is attempted after database work has drained. Normal host shutdown should be
allowed to complete this cleanup before its shutdown deadline expires.
