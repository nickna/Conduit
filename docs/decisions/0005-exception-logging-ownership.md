# ADR 0005: Log exceptions at outcome boundaries

- Status: Accepted
- Date: 2026-08-05
- Issue: #1307

## Context

Conduit had widespread wrappers of this form:

```csharp
try
{
    await ExecuteAsync();
}
catch (Exception exception)
{
    logger.LogError(exception, "Operation failed");
    throw;
}
```

The wrapper does not handle the exception or change its outcome. On HTTP paths,
the exception middleware logs the same exception again after mapping it to a
response and choosing the correct severity. This made client-caused `400` and
`404` responses produce an `Error` entry before the middleware correctly logged
them at a lower level. Similar wrappers around message handlers and hosted work
duplicated framework retry, dead-letter, and shutdown logs.

## Decision

An exception is logged once, at the outermost boundary that decides its outcome:

- HTTP exception middleware when producing a response;
- the messaging boundary when selecting retry or dead-letter behavior;
- a hosted-service loop when it decides to continue, back off, or terminate;
- a startup boundary when it decides whether the process can continue.

Services, repositories, provider clients, cache implementations, and handlers
must not catch an exception solely to call `LogError` and rethrow it unchanged.
They may catch when they perform meaningful work, including:

- translating or enriching the exception;
- returning a documented fallback or partial result;
- retrying, compensating, or changing durable state;
- selecting retry, dead-letter, degradation, or shutdown behavior.

Such catches log at the severity appropriate to the decision they make. A catch
that successfully handles an expected condition should not log it as an
unhandled error.

Operation identifiers belong in structured logging scopes, activity tags, or
the exception raised by the lower layer. Adding a `Warning` or `Debug` entry
immediately before an unchanged rethrow is not an acceptable substitute because
it still produces duplicate telemetry.

Recording metrics does not make a catch an outcome boundary. HTTP metrics
middleware may record an escaping exception and run cleanup, but leaves its
logging and severity selection to the outer exception middleware.

## Enforcement

`scripts/test/verify-exception-logging.ps1` parses tracked C# source and rejects
catch blocks whose only statements are a standard logging call followed by
`throw;`. This applies regardless of exception type, exception filter, or log
level. CI runs this guard after the .NET test suite.

The guard is deliberately structural: catches that recover, translate, retry,
or otherwise decide an outcome remain valid.
