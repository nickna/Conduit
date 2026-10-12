using System.Runtime.CompilerServices;

using ConduitLLM.PersistenceConverterTests;

using Npgsql;

#if CONDUIT_CONVERTER_COMPILED_MODEL
using ConduitLLM.Configuration.Data.CompiledModels;
#endif

if (args.Length != 0)
{
    Console.Error.WriteLine("Converter probe does not accept arguments.");
    return 2;
}
var configured = Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrWhiteSpace(configured))
{
    Console.Error.WriteLine("DATABASE_URL is required.");
    return 2;
}

#if !CONDUIT_CONVERTER_COMPILED_MODEL
Console.Error.WriteLine("Converter probe requires a published NativeAOT executable.");
return 2;
#else
if (RuntimeFeature.IsDynamicCodeSupported || RuntimeFeature.IsDynamicCodeCompiled)
{
    Console.Error.WriteLine("Converter probe requires dynamic code to be unavailable.");
    return 2;
}

string connectionString;
try { connectionString = NormalizeConnectionString(configured); }
catch (Exception exception)
{
    Console.Error.WriteLine("Converter probe configuration failed: " + exception.GetType().Name + ".");
    return 2;
}
var schema = PersistedValueConverterContract.SchemaPrefix + Guid.NewGuid().ToString("N");
Exception? workFailure = null;
Exception? cleanupFailure = null;
var roundTrips = 0;
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var bounded = new NpgsqlConnectionStringBuilder(connectionString)
    {
        Pooling = false, Timeout = 10, CommandTimeout = 10, CancellationTimeout = 1000
    };
    await using var connection = new NpgsqlConnection(bounded.ConnectionString);
    await connection.OpenAsync(deadline.Token);
    // This initializes the production checked-in runtime model and invokes its
    // real converter/comparer delegates. No EF query or update is executed.
    var model = ConduitDbContextModel.Instance;
    PersistedValueConverterContract.AssertInventory(model);
    await using (var create = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection))
        await create.ExecuteNonQueryAsync(deadline.Token);
    roundTrips = await PersistedValueConverterContract.ExercisePostgresAsync(
        connection, schema, model, PersistedValueConverterContract.Descriptors, deadline.Token);
}
catch (Exception exception) { workFailure = exception; }

// A fresh independent cleanup budget applies after work resources are disposed.
try { await PersistedValueConverterContract.CleanupAsync(connectionString, schema); }
catch (Exception exception) { cleanupFailure = exception; }
if (workFailure is not null)
    Console.Error.WriteLine("Converter probe failed: " + workFailure.GetType().Name + ".");
if (cleanupFailure is not null)
    Console.Error.WriteLine("Converter probe owned-schema cleanup failed: " + cleanupFailure.GetType().Name + ".");
if (workFailure is not null || cleanupFailure is not null) return 1;

Console.WriteLine("PASS: 31 persisted converter properties; model=compiled; dynamicCodeSupported=false; " +
    "dynamicCodeCompiled=false; PostgreSQLChecks=" + roundTrips +
    "; dictionaryComparers=2; ownedSchemaRemoved=true; EFQueryExecution=false.");
return 0;

static string NormalizeConnectionString(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "postgres" && uri.Scheme != "postgresql"))
        return new NpgsqlConnectionStringBuilder(value).ConnectionString;
    var credentials = uri.UserInfo.Split(':', 2);
    return new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        Username = Uri.UnescapeDataString(credentials[0]),
        Password = credentials.Length == 2 ? Uri.UnescapeDataString(credentials[1]) : string.Empty
    }.ConnectionString;
}
#endif
