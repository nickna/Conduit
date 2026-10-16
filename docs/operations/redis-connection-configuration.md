# Redis connection configuration

Admin, Gateway, Redis connection factories, and native dependency probes use
`RedisUrlParser` to apply the same configuration contract.

`REDIS_URL` takes precedence over `CONDUIT_REDIS_CONNECTION_STRING`. An absent
or blank primary value selects the fallback. An invalid nonblank primary value
fails startup, even when a valid fallback exists. Neither value configured means
Redis is unavailable; Gateway requires it, while Admin supports its documented
local-cache fallback.

Both variables accept these forms:

- A URI: `redis://host:6379`, `redis://:password@host:6379/2`, or
  `rediss://acl-user:password@host:6380/2`.
- A TCP endpoint, including a bracketed IPv6 address.
- A StackExchange.Redis options string, such as
  `host:6379,user=acl-user,password=password,defaultDatabase=2,ssl=true,sslHost=redis.example`.
  Multiple endpoints and recognized StackExchange.Redis options remain supported.

URI credentials use `username:password` or `:password`; empty passwords and
username-only credentials are rejected. Percent escapes are decoded exactly
once. Encode reserved characters in URI credentials. Values in an options string
are literal and are not percent-decoded.

URI paths may be absent, `/`, or a slash followed by a nonnegative database
number. Other paths and fragments are rejected. URI query keys are
case-insensitive and restricted to `connectTimeout`, `syncTimeout`,
`asyncTimeout`, `connectRetry`, `keepAlive`, `abortConnect`, `ssl`, and `sslHost`.
Timeouts must be positive integers; retry and keepalive values must be
nonnegative integers. Boolean values use `true` or `false`. Duplicate or unknown
keys fail validation. Select the database with the path; `defaultDatabase` is
available in an options string instead of the URI query.

`rediss` always requires TLS and rejects `ssl=false`. When TLS is enabled for a
URI, its original hostname becomes the explicit TLS identity unless `sslHost`
is supplied. Native loopback proxy routing preserves that identity when it
changes the transport endpoint. URI ports default to 6379. Endpoint/options
strings use StackExchange.Redis default ports: 6379, or 6380 when TLS is enabled.

The shared parser defaults `abortConnect=false`, `connectTimeout=10000`, and
`syncTimeout=10000` when these options are omitted. Redis connection factories
honor explicitly configured timeout and connection-attempt settings. The
application connection factory enables `allowAdmin` for its existing Redis
metrics capability; this policy is separate from parsing connection settings.

Commas, control characters, and leading or trailing whitespace in credentials or
`sslHost` are rejected before serializing options. StackExchange.Redis's options
syntax cannot preserve those values safely. Internal credential spaces can be
percent-encoded in a URI. Malformed percent escapes, unsupported options, and
invalid endpoints fail with credential-free diagnostics. Validation logs omit
the supplied configuration and parser exception text.

The typed `ParseConfiguration` and `ResolveConfiguration` APIs return
`ConfigurationOptions`. Existing string APIs serialize only validated options.
Treat those returned strings as credentials and avoid writing them to logs.

For PostgreSQL's separate connection contract, see
[PostgreSQL connection configuration](postgresql-connection-configuration.md).
