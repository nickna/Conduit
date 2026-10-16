# PostgreSQL connection configuration

Admin, Gateway, the release migrator, EF design-time tooling, and standalone
persistence/native probes use `PostgresConnectionStringParser` for `DATABASE_URL`.
The parser depends only on Npgsql and the base class library; standalone probes
link the same source without referencing Entity Framework.

`DATABASE_URL` accepts either a `postgres://` or `postgresql://` URI (scheme is
case-insensitive), or an Npgsql key/value connection string. Both forms require a
nonempty host, database, and username. Password is optional, allowing Npgsql's
alternative authentication mechanisms; configuring those mechanisms remains the
operator's responsibility. URI ports default to 5432 and bracketed IPv6 addresses
are supported.

For example, these forms preserve the same explicit credentials and options:

```text
postgresql://app:p%40ss%3Bword@[::1]:5432/conduit%20test?SSL%20Mode=Require&Maximum%20Pool%20Size=80
Host=::1;Port=5432;Database=conduit test;Username=app;Password="p@ss;word";SSL Mode=Require;Maximum Pool Size=80
```

URI usernames, passwords, database names, and query keys/values are percent-decoded
once. A literal plus remains a plus; use `%20` for a space. Every percent escape
must contain two hexadecimal digits. URI fragments are rejected. Query parameters
must use `key=value`; Npgsql validates each option and its type. Supported Npgsql
aliases and casing work in both forms. Duplicate options, including aliases for
the same option, use the last supplied value, matching Npgsql key/value parsing.
Query options can explicitly override URI fields such as host, username, or
database. A standalone loopback proxy additionally rejects endpoint query overrides
so they cannot bypass its owned route.

Decoded values are assigned individually to `NpgsqlConnectionStringBuilder`.
Semicolons, quotes, or equals signs inside a password remain part of the password,
and cannot create another connection option. Invalid schemes, missing required
fields, malformed escapes, unknown options, invalid typed values, and an effective
minimum pool size greater than the maximum are rejected with a fixed diagnostic
that includes neither the supplied configuration nor a provider inner exception.

Runtime hosts apply pool defaults independently to each absent option. An explicit
value is retained even when supplied through an alias or different casing. Admin
uses minimum/maximum pool sizes of 5/75; Gateway uses 10/150; WebAdmin uses 2/20;
generic Core callers use 1/100. Host defaults also enable pooling and include error
detail, set connection lifetime to 300 seconds, and idle lifetime to 60 seconds.
`Pooling=false` remains disabled. Set both pool sizes explicitly when selecting a
maximum below the host's default minimum. Migration and standalone tools apply
Npgsql defaults to options not supplied by the operator.

Connection diagnostics are rebuilt from the typed configuration with `Password`
and `SSL Password` masked. Quoted semicolons and quotes cannot escape redaction.
Malformed input is represented by a fixed invalid-configuration marker. See
[database pool ownership](database-pool-ownership.md) for host and connection
lease lifetimes.
