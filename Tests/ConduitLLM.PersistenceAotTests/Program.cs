using System.Globalization;

using Npgsql;

var configured = Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrWhiteSpace(configured))
{
    Console.Error.WriteLine("DATABASE_URL is required.");
    return 2;
}

var connectionString = NormalizeConnectionString(configured);
var schema = $"conduit_aot_probe_{Guid.NewGuid():N}";

try
{
    await using var connection = new NpgsqlConnection(connectionString);
    await OpenWithRetryAsync(connection);
    await ExecuteAsync(connection, $"""
        CREATE SCHEMA "{schema}";
        CREATE TABLE "{schema}".records (
            id uuid PRIMARY KEY,
            version bigint NOT NULL,
            amount numeric(18,6) NOT NULL,
            payload jsonb NOT NULL,
            created_at timestamptz NOT NULL
        );
        """);

    var id = Guid.NewGuid();
    var createdAt = DateTimeOffset.UtcNow;
    await using (var insert = new NpgsqlCommand(
        $"INSERT INTO \"{schema}\".records VALUES (@id, 0, @amount, @payload::jsonb, @created)", connection))
    {
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("amount", 12.345678m);
        insert.Parameters.AddWithValue("payload", "{\"kind\":\"native\"}");
        insert.Parameters.AddWithValue("created", createdAt);
        Ensure(await insert.ExecuteNonQueryAsync() == 1, "write failed");
    }

    await using (var read = new NpgsqlCommand(
        $"SELECT amount, payload->>'kind', created_at FROM \"{schema}\".records WHERE id=@id", connection))
    {
        read.Parameters.AddWithValue("id", id);
        await using var reader = await read.ExecuteReaderAsync();
        Ensure(await reader.ReadAsync(), "read failed");
        Ensure(reader.GetDecimal(0) == 12.345678m, "numeric mapping failed");
        Ensure(reader.GetString(1) == "native", "jsonb mapping failed");
        Ensure(reader.GetFieldValue<DateTime>(2).Kind == DateTimeKind.Utc, "timestamptz mapping failed");
    }

    await using (var transaction = await connection.BeginTransactionAsync())
    {
        await using var rolledBack = new NpgsqlCommand(
            $"INSERT INTO \"{schema}\".records VALUES (@id, 0, 1, '{{}}', now())", connection, transaction);
        rolledBack.Parameters.AddWithValue("id", Guid.NewGuid());
        await rolledBack.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }
    Ensure(await ScalarAsync<long>(connection, $"SELECT count(*) FROM \"{schema}\".records") == 1, "rollback failed");

    var updates = await Task.WhenAll(
        TryOptimisticUpdateAsync(connectionString, schema, id),
        TryOptimisticUpdateAsync(connectionString, schema, id));
    Ensure(updates.Sum() == 1, "optimistic concurrency failed");

    var attempts = 0;
    await RetrySerializationAsync(async () =>
    {
        attempts++;
        if (attempts == 1)
        {
            await ExecuteAsync(connection, "SET SESSION application_name = 'conduit-aot-retry-ready'");
            await ExecuteAsync(connection, "DO $$ BEGIN RAISE EXCEPTION 'retry probe' USING ERRCODE='40001'; END $$;");
        }
        return await ScalarAsync<int>(connection, "SELECT 1");
    });
    Ensure(attempts == 2, "serialization retry failed");

    Console.WriteLine("Native persistence probe passed: reads, writes, rollback, concurrency, retry, and PostgreSQL mappings.");
    return 0;
}
finally
{
    try
    {
        await using var cleanup = new NpgsqlConnection(connectionString);
        await cleanup.OpenAsync();
        await ExecuteAsync(cleanup, $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Cleanup failed: {exception.Message}");
    }
}

static string NormalizeConnectionString(string configured)
{
    if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
    {
        return configured;
    }

    var credentials = uri.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = uri.AbsolutePath.TrimStart('/'),
        Username = Uri.UnescapeDataString(credentials[0]),
        Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty
    }.ConnectionString;
}

static async Task OpenWithRetryAsync(NpgsqlConnection connection)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await connection.OpenAsync();
            return;
        }
        catch (NpgsqlException) when (attempt < 4)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
        }
    }
}

static async Task<int> TryOptimisticUpdateAsync(string connectionString, string schema, Guid id)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(
        $"UPDATE \"{schema}\".records SET version=version+1 WHERE id=@id AND version=0", connection);
    command.Parameters.AddWithValue("id", id);
    return await command.ExecuteNonQueryAsync();
}

static async Task<T> RetrySerializationAsync<T>(Func<Task<T>> operation)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            return await operation();
        }
        catch (PostgresException exception) when (exception.SqlState == "40001" && attempt < 3)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
        }
    }
}

static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}

static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
{
    await using var command = new NpgsqlCommand(sql, connection);
    var value = await command.ExecuteScalarAsync();
    return (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
