#nullable enable

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using ConduitLLM.Core.Data;
using ConduitLLM.Core.Data.Health;
using ConduitLLM.Core.Data.Interfaces;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Core.Data;

public sealed class DatabaseConnectionFactoryLifecycleTests
{
    [Fact]
    public async Task FailedOpen_DisposesTheUnreturnedConnectionAndRetainsTheProviderFailure()
    {
        var failure = new IOException("provider open failed");
        var connection = new RecordingConnection { OpenOperation = _ => Task.FromException(failure) };
        var factory = new RecordingFactory(() => connection);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateConnectionAsync());

        Assert.Same(failure, exception.InnerException);
        Assert.Contains("postgres", exception.Message);
        Assert.Equal(1, connection.OpenCalls);
        AssertAsyncConnectionDisposal(connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledOpen_AwaitsAsyncCleanupBeforeCompleting(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = cancelled ? (Exception)new OperationCanceledException(cancellation.Token) : new IOException("open failed");
        var cleanupEntered = NewSignal();
        var allowCleanup = NewSignal();
        var connection = new RecordingConnection
        {
            OpenOperation = _ =>
            {
                if (cancelled) cancellation.Cancel();
                return Task.FromException(failure);
            },
            DisposeOperation = async () =>
            {
                cleanupEntered.TrySetResult();
                await allowCleanup.Task;
            }
        };
        var pending = new RecordingFactory(() => connection).CreateConnectionAsync(cancellation.Token);
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
        }
        finally { allowCleanup.TrySetResult(); }

        if (cancelled)
            Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() => pending));
        else
            Assert.Same(failure, (await Assert.ThrowsAsync<InvalidOperationException>(() => pending)).InnerException);
        AssertAsyncConnectionDisposal(connection);
    }

    [Fact]
    public async Task CancelledOpen_PreservesTheOriginalCallerCancellationAndDisposesTheConnection()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException("open cancelled", cancellation.Token);
        var connection = new RecordingConnection
        {
            OpenOperation = token =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                return Task.FromException(original);
            }
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new RecordingFactory(() => connection).CreateConnectionAsync(cancellation.Token));

        Assert.Same(original, exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        AssertAsyncConnectionDisposal(connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailure_DoesNotReplaceTheOpenFailureOrCallerCancellation(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var failure = cancelled ? (Exception)new OperationCanceledException(cancellation.Token) : new IOException("open failed");
        var cleanupFailure = new IOException("connection cleanup failed");
        var logger = new RecordingLogger<DatabaseConnectionFactory>();
        var connection = new RecordingConnection
        {
            OpenOperation = _ =>
            {
                if (cancelled) cancellation.Cancel();
                return Task.FromException(failure);
            },
            DisposeOperation = () => ValueTask.FromException(cleanupFailure)
        };
        var factory = new RecordingFactory(() => connection, logger);

        if (cancelled)
            Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() => factory.CreateConnectionAsync(cancellation.Token)));
        else
            Assert.Same(failure, (await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateConnectionAsync())).InnerException);

        AssertAsyncConnectionDisposal(connection);
        Assert.Contains(logger.Exceptions, exception => ReferenceEquals(exception, cleanupFailure));
    }

    [Fact]
    public async Task ProviderCancellation_WithAnUncancelledCallerToken_IsWrappedAsAProviderFailure()
    {
        using var caller = new CancellationTokenSource();
        var providerCancellation = new OperationCanceledException(new CancellationToken(canceled: true));
        var connection = new RecordingConnection { OpenOperation = _ => Task.FromException(providerCancellation) };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RecordingFactory(() => connection).CreateConnectionAsync(caller.Token));

        Assert.False(caller.IsCancellationRequested);
        Assert.Same(providerCancellation, exception.InnerException);
        AssertAsyncConnectionDisposal(connection);
    }

    [Fact]
    public async Task PreCancelledOpen_DoesNotCreateAConnection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var connection = new RecordingConnection();
        var factory = new RecordingFactory(() => connection);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => factory.CreateConnectionAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, factory.CreationCalls);
        Assert.Equal(0, connection.OpenCalls);
        Assert.Equal(0, connection.AsyncDisposeCalls);
        Assert.Equal(0, connection.SyncDisposeCalls);
    }

    [Fact]
    public async Task SuccessfulOpen_TransfersOwnershipToTheCaller()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new RecordingConnection();
        var factory = new RecordingFactory(() => connection);

        var returned = await factory.CreateConnectionAsync(cancellation.Token);

        Assert.Same(connection, returned);
        Assert.Equal(ConnectionState.Open, returned.State);
        Assert.Equal(cancellation.Token, connection.OpenToken);
        Assert.Equal(0, connection.AsyncDisposeCalls);
        Assert.Equal(0, connection.SyncDisposeCalls);
        await returned.DisposeAsync();
        AssertAsyncConnectionDisposal(connection);
    }

    [Fact]
    public async Task CreationFailure_RetainsItsOriginalDiagnostic()
    {
        var original = new IOException("create failed");
        var factory = new RecordingFactory(() => throw original);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateConnectionAsync());

        Assert.Same(original, exception.InnerException);
        Assert.Equal(1, factory.CreationCalls);
    }

    [Fact]
    public async Task HealthyProbe_ExecutesItsQueryAndDisposesCommandAndConnectionAsynchronously()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new RecordingConnection();
        var health = CreateHealth(connection);

        var result = await health.CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("SELECT 1", connection.Command.CommandText);
        Assert.Equal(cancellation.Token, connection.OpenToken);
        Assert.Equal(cancellation.Token, connection.Command.ExecutionToken);
        Assert.Equal(1, connection.Command.ExecutionCalls);
        AssertAsyncProbeDisposal(connection);
    }

    [Fact]
    public async Task HealthyProbe_AwaitsBothAsyncCleanupOperationsBeforeReturning()
    {
        var commandCleanupEntered = NewSignal();
        var connectionCleanupEntered = NewSignal();
        var allowCommandCleanup = NewSignal();
        var allowConnectionCleanup = NewSignal();
        var connection = new RecordingConnection
        {
            DisposeOperation = async () =>
            {
                connectionCleanupEntered.TrySetResult();
                await allowConnectionCleanup.Task;
            }
        };
        connection.Command.DisposeOperation = async () =>
        {
            commandCleanupEntered.TrySetResult();
            await allowCommandCleanup.Task;
        };
        var pending = CreateHealth(connection).CheckHealthAsync(new HealthCheckContext());
        try
        {
            await commandCleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            allowCommandCleanup.TrySetResult();
            await connectionCleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            allowCommandCleanup.TrySetResult();
            allowConnectionCleanup.TrySetResult();
        }

        Assert.Equal(HealthStatus.Healthy, (await pending).Status);
        AssertAsyncProbeDisposal(connection);
    }

    [Fact]
    public async Task HealthOpenFailure_ReturnsUnhealthyAndKeepsTheOriginalProviderDiagnostic()
    {
        var original = new IOException("open failed");
        var connection = new RecordingConnection { OpenOperation = _ => Task.FromException(original) };

        var result = await CreateHealth(connection).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(original, Assert.IsType<InvalidOperationException>(result.Exception).InnerException);
        AssertAsyncConnectionDisposal(connection);
        Assert.Equal(0, connection.Command.ExecutionCalls);
        Assert.Equal(0, connection.Command.AsyncDisposeCalls);
    }

    [Fact]
    public async Task HealthOpenCancellation_PropagatesCallerCancellationAndDisposesTheFailedOpen()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(cancellation.Token);
        var connection = new RecordingConnection
        {
            OpenOperation = _ =>
            {
                cancellation.Cancel();
                return Task.FromException(original);
            }
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateHealth(connection).CheckHealthAsync(new HealthCheckContext(), cancellation.Token));

        Assert.Same(original, exception);
        AssertAsyncConnectionDisposal(connection);
        Assert.Equal(0, connection.Command.AsyncDisposeCalls);
    }

    [Fact]
    public async Task HealthCommandFailure_ReturnsUnhealthyAndDisposesBothResources()
    {
        var original = new IOException("query failed");
        var connection = new RecordingConnection();
        connection.Command.ExecuteOperation = _ => Task.FromException<object?>(original);

        var result = await CreateHealth(connection).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(original, result.Exception);
        AssertAsyncProbeDisposal(connection);
    }

    [Fact]
    public async Task HealthCommandCancellation_PropagatesTheOriginalCallerCancellationAndDisposesBothResources()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(cancellation.Token);
        var connection = new RecordingConnection();
        connection.Command.ExecuteOperation = _ =>
        {
            cancellation.Cancel();
            return Task.FromException<object?>(original);
        };

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateHealth(connection).CheckHealthAsync(new HealthCheckContext(), cancellation.Token));

        Assert.Same(original, exception);
        AssertAsyncProbeDisposal(connection);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthCleanupFailures_DoNotReplaceCommandFailureOrCallerCancellation(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var original = cancelled ? (Exception)new OperationCanceledException(cancellation.Token) : new IOException("query failed");
        var commandCleanupFailure = new IOException("command cleanup failed");
        var connectionCleanupFailure = new IOException("connection cleanup failed");
        var connection = new RecordingConnection { DisposeOperation = () => ValueTask.FromException(connectionCleanupFailure) };
        connection.Command.ExecuteOperation = _ =>
        {
            if (cancelled) cancellation.Cancel();
            return Task.FromException<object?>(original);
        };
        connection.Command.DisposeOperation = () => ValueTask.FromException(commandCleanupFailure);
        var logger = new RecordingLogger<DatabaseHealthCheck>();
        var health = CreateHealth(connection, logger);

        if (cancelled)
            Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() => health.CheckHealthAsync(new HealthCheckContext(), cancellation.Token)));
        else
        {
            var result = await health.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Same(original, result.Exception);
        }

        AssertAsyncProbeDisposal(connection);
        Assert.Contains(logger.Exceptions, exception => ReferenceEquals(exception, commandCleanupFailure));
        Assert.Contains(logger.Exceptions, exception => ReferenceEquals(exception, connectionCleanupFailure));
    }

    [Fact]
    public async Task HealthProviderCommandCancellation_WithUncancelledCallerToken_ReturnsUnhealthy()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new OperationCanceledException(new CancellationToken(canceled: true));
        var connection = new RecordingConnection();
        connection.Command.ExecuteOperation = _ => Task.FromException<object?>(original);

        var result = await CreateHealth(connection).CheckHealthAsync(new HealthCheckContext(), cancellation.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Same(original, result.Exception);
        AssertAsyncProbeDisposal(connection);
    }

    [Fact]
    public async Task PreCancelledHealth_DoesNotCreateAConnection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var factory = new RecordingFactory(() => new RecordingConnection());
        var health = new DatabaseHealthCheck(factory, NullLogger<DatabaseHealthCheck>.Instance);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.CheckHealthAsync(new HealthCheckContext(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(0, factory.CreationCalls);
    }

    private static DatabaseHealthCheck CreateHealth(RecordingConnection connection, ILogger<DatabaseHealthCheck>? logger = null) =>
        new(new RecordingFactory(() => connection), logger ?? NullLogger<DatabaseHealthCheck>.Instance);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertAsyncConnectionDisposal(RecordingConnection connection)
    {
        Assert.Equal(1, connection.AsyncDisposeCalls);
        Assert.Equal(0, connection.SyncDisposeCalls);
    }

    private static void AssertAsyncProbeDisposal(RecordingConnection connection)
    {
        AssertAsyncConnectionDisposal(connection);
        Assert.Equal(1, connection.Command.AsyncDisposeCalls);
        Assert.Equal(0, connection.Command.SyncDisposeCalls);
    }

    private sealed class RecordingFactory(Func<DbConnection> create, ILogger<DatabaseConnectionFactory>? logger = null)
        : DatabaseConnectionFactory(CreateManager(), logger ?? NullLogger<DatabaseConnectionFactory>.Instance)
    {
        public int CreationCalls { get; private set; }

        public override DbConnection CreateConnection()
        {
            CreationCalls++;
            return create();
        }

        private static IConnectionStringManager CreateManager()
        {
            var manager = new Mock<IConnectionStringManager>();
            manager.Setup(value => value.GetProviderAndConnectionString(It.IsAny<Action<string>>()))
                .Returns(("postgres", "Host=unused;Database=unused;Username=unused;Password=test-only"));
            return manager.Object;
        }
    }

    private sealed class RecordingConnection : DbConnection
    {
        private ConnectionState _state;
        public RecordingCommand Command { get; } = new();
        public Func<CancellationToken, Task> OpenOperation { get; init; } = _ => Task.CompletedTask;
        public Func<ValueTask> DisposeOperation { get; init; } = () => ValueTask.CompletedTask;
        public int OpenCalls { get; private set; }
        public int AsyncDisposeCalls { get; private set; }
        public int SyncDisposeCalls { get; private set; }
        public CancellationToken OpenToken { get; private set; }
        [AllowNull] public override string ConnectionString { get; set; } = "recording";
        public override string Database => "recording";
        public override string DataSource => "recording";
        public override string ServerVersion => "1";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => throw new NotSupportedException("Only asynchronous opening is expected.");

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            OpenCalls++;
            OpenToken = cancellationToken;
            await OpenOperation(cancellationToken);
            _state = ConnectionState.Open;
        }

        public override async ValueTask DisposeAsync()
        {
            AsyncDisposeCalls++;
            await DisposeOperation();
            _state = ConnectionState.Closed;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SyncDisposeCalls++;
            base.Dispose(disposing);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => Command;
    }

    private sealed class RecordingCommand : DbCommand
    {
        public Func<CancellationToken, Task<object?>> ExecuteOperation { get; set; } = _ => Task.FromResult<object?>(1);
        public Func<ValueTask> DisposeOperation { get; set; } = () => ValueTask.CompletedTask;
        public int ExecutionCalls { get; private set; }
        public int AsyncDisposeCalls { get; private set; }
        public int SyncDisposeCalls { get; private set; }
        public CancellationToken ExecutionToken { get; private set; }
        [AllowNull] public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; }
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException("Only asynchronous execution is expected.");
        public override void Prepare() => throw new NotSupportedException();
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

        public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        {
            ExecutionCalls++;
            ExecutionToken = cancellationToken;
            return ExecuteOperation(cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            AsyncDisposeCalls++;
            await DisposeOperation();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SyncDisposeCalls++;
            base.Dispose(disposing);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<Exception?> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Exceptions.Add(exception);
    }
}
