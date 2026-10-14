using ConduitLLM.Functions.Enums;
using ConduitLLM.Persistence.Npgsql;

using Npgsql;

internal static class FunctionExecutionConfigurationPersistenceProbe
{
    // Runs after the discovery fixture in the same caller-owned disposable schema.
    // The only new table is FunctionCredentials; no runtime Functions implementation
    // or credential decryption is imported by this persistence probe.
    internal static async Task RunAsync(NpgsqlDataSource source)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Require(new NpgsqlConnectionStringBuilder(source.ConnectionString).MaxPoolSize == 1, "single-slot fixture");
        await using (var connection = await source.OpenConnectionAsync(deadline.Token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "FunctionCredentials" (
                    "Id" integer PRIMARY KEY, "ProviderType" integer NOT NULL,
                    "FunctionConfigurationId" integer NULL, "ApiKey" varchar(2000) NULL,
                    "BaseUrl" varchar(500) NULL, "Organization" varchar(200) NULL,
                    "FunctionAccountGroup" smallint NOT NULL, "IsPrimary" boolean NOT NULL,
                    "IsEnabled" boolean NOT NULL, "KeyName" varchar(200) NULL,
                    "CreatedAt" timestamptz NOT NULL, "UpdatedAt" timestamptz NOT NULL);
                INSERT INTO "FunctionCredentials" VALUES
                    (100, @exa, NULL, 'global plaintext', NULL, NULL, 0, TRUE, TRUE, 'B global', @created, @updated),
                    (101, @exa, 11, @opaque, 'https://example.test/scoped', 'O''Hare 雪', 32, TRUE, TRUE, 'A scoped', @created, @updated),
                    (102, @exa, 12, 'enc:v1:invalid', '', '', -7, FALSE, FALSE, '0 secondary', @created, @updated),
                    (103, @exa, NULL, NULL, NULL, NULL, 0, FALSE, TRUE, NULL, @created, @updated),
                    (104, 197, NULL, NULL, NULL, NULL, 32767, FALSE, TRUE, NULL, @created, @updated);
                """;
            command.Parameters.AddWithValue("exa", (int)FunctionProviderType.Exa);
            command.Parameters.AddWithValue("opaque", "enc:v1:opaque=='雪'");
            var created = new DateTime(2026, 10, 11, 7, 0, 0, DateTimeKind.Utc);
            command.Parameters.AddWithValue("created", created);
            command.Parameters.AddWithValue("updated", created.AddMinutes(1));
            await command.ExecuteNonQueryAsync(deadline.Token);
        }

        var store = new NpgsqlFunctionExecutionConfigurationStore(source);
        var disabled = await store.GetConfigurationAsync(12, deadline.Token);
        Require(disabled is { IsEnabled: false, ConfigurationName: "Disabled" } &&
            disabled.CostMappings.Count == 0 && disabled.Executions.Count == 0, "disabled scalar configuration");
        Require(await store.GetConfigurationAsync(-1, deadline.Token) is null, "missing configuration");
        var unknownConfiguration = await store.GetConfigurationAsync(13, deadline.Token);
        Require(unknownConfiguration is not null && (int)unknownConfiguration.ProviderType == 197 &&
            (int)unknownConfiguration.Purpose == -9 && (int)unknownConfiguration.DefaultExecutionMode == 123,
            "unknown configuration enums");
        var configurations = await store.GetConfigurationsAsync(new[] { 11, 12, 11, 13, -1 }, deadline.Token);
        Require(configurations.Select(configuration => configuration.Id).Order().SequenceEqual(new[] { 11, 12, 13 }),
            "distinct existing IDs including disabled");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Require((await store.GetConfigurationsAsync(null, cancelled.Token)).Count == 0 &&
                (await store.GetConfigurationsAsync(Array.Empty<int>(), cancelled.Token)).Count == 0,
                "cancelled empty ID input");
            try
            {
                await store.GetConfigurationsAsync(new[] { 11 }, cancelled.Token);
                throw new InvalidOperationException("Native execution configuration read ignored cancellation.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }

        var credentials = await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, deadline.Token);
        Require(credentials.Select(credential => credential.Id).SequenceEqual(new[] { 101, 100, 102, 103 }),
            "primary/key-name/null ordering");
        Require(credentials.Single(credential => credential.Id == 100).ApiKey == "global plaintext",
            "legacy plaintext credential");
        var scoped = credentials.Single(credential => credential.Id == 101);
        Require(scoped.ProviderType == FunctionProviderType.Exa && scoped.FunctionConfigurationId == 11 &&
            scoped.ApiKey == "enc:v1:opaque=='雪'" && scoped.BaseUrl == "https://example.test/scoped" &&
            scoped.Organization == "O'Hare 雪" && scoped.FunctionAccountGroup == 32 && scoped.IsPrimary &&
            scoped.IsEnabled && scoped.KeyName == "A scoped" && scoped.CreatedAt.Kind == DateTimeKind.Utc &&
            scoped.UpdatedAt == scoped.CreatedAt.AddMinutes(1), "all credential scalars and opaque ciphertext");
        var other = credentials.Single(credential => credential.Id == 102);
        Require(other.FunctionConfigurationId == 12 && !other.IsPrimary && !other.IsEnabled && other.ApiKey == "enc:v1:invalid" &&
            other.BaseUrl == "" && other.Organization == "" && other.FunctionAccountGroup == -7,
            "disabled other-configuration credentials and empty values");
        scoped.ApiKey = "detached mutation";
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, deadline.Token))
            .Single(credential => credential.Id == 101).ApiKey == "enc:v1:opaque=='雪'", "detached credentials");
        var unknownCredential = (await store.ListCredentialsForProviderAsync((FunctionProviderType)197, deadline.Token)).Single();
        Require(unknownCredential.Id == 104 && (int)unknownCredential.ProviderType == 197 &&
            unknownCredential.FunctionAccountGroup == short.MaxValue && unknownCredential.FunctionConfigurationId is null &&
            unknownCredential.ApiKey is null && unknownCredential.BaseUrl is null && unknownCredential.Organization is null &&
            unknownCredential.KeyName is null, "unknown provider and nullable credential scalars");
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Tavily, deadline.Token)).Count == 0,
            "missing credential provider");

        await using (await source.OpenConnectionAsync(deadline.Token))
        {
            using var cancelled = new CancellationTokenSource();
            var read = store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, cancelled.Token);
            try
            {
                Require(!read.IsCompleted, "read waiting on occupied pool slot");
                cancelled.Cancel();
                try
                {
                    await read.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("Native credential pool wait ignored cancellation.");
                }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }
            finally
            {
                cancelled.Cancel();
                try { await read.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            }
        }
        Require((await store.ListCredentialsForProviderAsync(FunctionProviderType.Exa, deadline.Token)).Count == 4,
            "single-slot reuse after blocked acquisition cancellation");
        Console.WriteLine("Function execution configuration persistence passed: scalar/disabled/distinct-ID reads, ordered opaque credentials, detached values and cancelled single-slot pool acquisition/reuse.");
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition) throw new InvalidOperationException($"Native function execution configuration contract failed: {contract}.");
    }
}
