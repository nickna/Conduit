using ConduitLLM.IntegrationTests.Infrastructure;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed class BoundedExceptionDiagnosticsTests
{
    [Fact]
    public void RetainsOriginalExceptionTypesAndProviderCodeWithoutFormattingPrivateState()
    {
        const string secret = "private-password-request-and-sql";
        var diagnostics = new BoundedExceptionDiagnostics();
        var logger = diagnostics.CreateLogger<BoundedExceptionDiagnosticsTests>();
        var failure = new IOException(secret,
            new PostgresException(secret, "ERROR", "ERROR", PostgresErrorCodes.ConnectionFailure,
                detail: secret, internalQuery: secret));
        failure.Data[secret] = secret;
        using (logger.BeginScope(secret))
            logger.Log(LogLevel.Error, new EventId(17, secret), secret, failure,
                static (_, _) => throw new InvalidOperationException("Private formatter must not run."));

        var snapshot = diagnostics.Snapshot();
        Assert.Contains(typeof(BoundedExceptionDiagnosticsTests).FullName!, snapshot);
        Assert.Contains("event=17", snapshot);
        Assert.Contains("type=System.IO.IOException", snapshot);
        Assert.Contains("type=Npgsql.PostgresException SQLSTATE=08006", snapshot);
        Assert.DoesNotContain(secret, snapshot);
    }

    [Fact]
    public void RetainsOnlyBoundedRecentRecordsAndBoundsAggregateTraversal()
    {
        var diagnostics = new BoundedExceptionDiagnostics();
        var logger = diagnostics.CreateLogger<BoundedExceptionDiagnosticsTests>();
        var failures = new AggregateException(Enumerable.Range(0, 100)
            .Select(_ => new IOException("private-message")));
        Parallel.For(0, BoundedExceptionDiagnostics.MaximumRecords * 4, index =>
            logger.Log(LogLevel.Error, new EventId(index), "private-state", failures,
                static (_, _) => throw new InvalidOperationException("Private formatter must not run.")));
        logger.Log(LogLevel.Error, new EventId(999), "private-state", new IOException("private-message"),
            static (_, _) => throw new InvalidOperationException("Private formatter must not run."));

        var records = diagnostics.Snapshot().Split(Environment.NewLine);
        Assert.Equal(BoundedExceptionDiagnostics.MaximumRecords, records.Length);
        Assert.Contains("event=999", records[^1]);
        Assert.All(records, record =>
        {
            Assert.InRange(record.Split("type=", StringSplitOptions.None).Length - 1, 1, 8);
            Assert.True(record.Length < 2048);
            Assert.DoesNotContain("private-", record);
        });
    }

    [Fact]
    public void RejectsProviderCodesThatAreNotSqlStateSymbols()
    {
        var diagnostics = new BoundedExceptionDiagnostics();
        var logger = diagnostics.CreateLogger<BoundedExceptionDiagnosticsTests>();
        logger.LogError(new PostgresException("private-message", "ERROR", "ERROR", "private-sql-state"),
            "private-state");
        logger.LogInformation("private-information");

        var snapshot = diagnostics.Snapshot();
        Assert.Contains("type=Npgsql.PostgresException", snapshot);
        Assert.DoesNotContain("SQLSTATE=", snapshot);
        Assert.DoesNotContain("private-", snapshot);
        Assert.Single(snapshot.Split(Environment.NewLine));
    }
}
