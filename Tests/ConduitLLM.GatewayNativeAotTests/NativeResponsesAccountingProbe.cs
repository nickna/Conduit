using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

/// <summary>
/// Published-process regression for real provider JSON and post-provider error billing.
/// A fresh key/group keeps the existing parity fixture's exact accounting independent.
/// Rows intentionally remain in the disposable test database for audit/shutdown proof.
/// </summary>
internal static class NativeResponsesAccountingProbe
{
    internal const string TextPrompt = NativeProviderStub.ResponsesTextPrompt;
    internal const string UnsupportedPrompt = NativeProviderStub.ResponsesUnsupportedPrompt;
    internal const string ToolPrompt = NativeProviderStub.ResponsesToolPrompt;
    internal const string Text = NativeProviderStub.ResponsesText;
    private const decimal Price = 0.000075m;

    public static async Task RunAsync(Uri gateway, Uri provider, string postgres, string redis, CancellationToken cancellationToken)
    {
        Require(gateway.IsLoopback && provider.IsLoopback, "Responses proof requires loopback services.");
        var owner = "native-responses-" + Guid.NewGuid().ToString("N");
        var key = "condt_" + Guid.NewGuid().ToString("N");
        await using var source = NpgsqlDataSource.Create(postgres);
        var (groupId, keyId) = await CreateFixtureAsync(source, owner, key, cancellationToken);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var initialCalls = await ProviderCountAsync(http, provider, cancellationToken);
        var billingIds = new HashSet<string>(StringComparer.Ordinal);
        string? toolBillingId = null;
        foreach (var prompt in new[] { TextPrompt, UnsupportedPrompt, ToolPrompt })
        {
            using var request = Request(gateway, key, prompt, store: false);
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Require(body.Length < 65536, "Unexpected Responses proof body size.");
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var billingId = response.Headers.GetValues("x-request-id").Single();
            Require(billingIds.Add(billingId), "Responses requests reused a billing ID.");
            if (prompt == TextPrompt)
            {
                Require(response.StatusCode == HttpStatusCode.OK, "Valid provider text did not produce HTTP200.");
                Require(root.GetProperty("object").GetString() == "response" &&
                        root.GetProperty("status").GetString() == "completed" &&
                        root.GetProperty("model").GetString() == NativeParityFixture.ModelAlias &&
                        root.GetProperty("output_text").GetString() == Text, "Completed Responses contract changed.");
                var output = root.GetProperty("output");
                Require(output.GetArrayLength() == 1 &&
                        output[0].GetProperty("content")[0].GetProperty("text").GetString() == Text,
                    "Responses output text differs.");
                var usage = root.GetProperty("usage");
                Require(usage.GetProperty("input_tokens").GetInt32() == 10 &&
                        usage.GetProperty("output_tokens").GetInt32() == 5 &&
                        usage.GetProperty("total_tokens").GetInt32() == 15, "Responses usage changed.");
            }
            else
            {
                Require(response.StatusCode == HttpStatusCode.BadGateway &&
                        root.GetProperty("error").GetProperty("code").GetString() == "provider_response_error",
                    "Unsupported provider output was accepted or mistranslated.");
                if (prompt == ToolPrompt) toolBillingId = billingId;
            }
        }

        using (var invalid = Request(gateway, key, TextPrompt, store: true))
        using (var response = await http.SendAsync(invalid, cancellationToken))
            Require(response.StatusCode == HttpStatusCode.BadRequest, "Unsupported store:true was accepted.");

        await AssertDurableAsync(source, groupId, keyId, cancellationToken);
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis);
        var db = multiplexer.GetDatabase();
        var settled = await db.HashGetAllAsync($"spend_reservations_settled:group:{groupId}");
        var unresolved = await db.HashGetAllAsync($"spend_reservations_indeterminate:group:{groupId}");
        Require(settled.Length == 2 && unresolved.Length == 1, "Completed/error reservation states were not preserved.");
        foreach (var row in settled)
            Require(billingIds.Contains(row.Name.ToString()) && Parse(row.Value.ToString()) == 7500m,
                "Resolved provider usage did not settle exactly once.");
        Require(unresolved[0].Name.ToString() == toolBillingId, "Wrong request retained an unresolved hold.");
        using (var record = JsonDocument.Parse(unresolved[0].Value.ToString()))
        {
            var r = record.RootElement;
            Require(!r.GetProperty("resolved").GetBoolean() &&
                    Parse(r.GetProperty("knownUnits").ToString()) == 7500m &&
                    Parse(r.GetProperty("heldAmount").ToString()) > 0,
                "Unsupported tool output lost known spend or released its unresolved bound.");
        }
        Require(await ProviderCountAsync(http, provider, cancellationToken) - initialCalls == 3,
            "Provider invocation was replayed or a pre-provider rejection invoked the provider.");
        // A second read must not change accounting or trigger a provider retry.
        await AssertDurableAsync(source, groupId, keyId, cancellationToken);
        Require(await ProviderCountAsync(http, provider, cancellationToken) - initialCalls == 3,
            "Financial verification replayed provider work.");
    }

    private static HttpRequestMessage Request(Uri gateway, string key, string prompt, bool store)
    {
        // All interpolated values are internal constants; no arbitrary text is inserted into JSON.
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(gateway, "/v1/responses"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent($$"""{"model":"native-aot-model","input":"{{prompt}}","store":{{(store ? "true" : "false")}},"stream":false}""", Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<int> ProviderCountAsync(HttpClient http, Uri provider, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await http.GetStringAsync(new Uri(provider, "/probe/state"), token));
        return document.RootElement.GetProperty("responses_proof_requests").GetInt32();
    }

    private static async Task<(int GroupId, int KeyId)> CreateFixtureAsync(NpgsqlDataSource source, string owner, string key, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using var group = connection.CreateCommand();
        group.Transaction = transaction;
        group.CommandText = """
            INSERT INTO "VirtualKeyGroups" ("ExternalGroupId","GroupName","Balance","LifetimeCreditsAdded","LifetimeSpent","CreatedAt","UpdatedAt")
            VALUES (@owner,'Responses accounting proof',100,100,0,@now,@now) RETURNING "Id"
            """;
        group.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, owner);
        group.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        var groupId = (int)(await group.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("No fixture group."));
        await using var virtualKey = connection.CreateCommand();
        virtualKey.Transaction = transaction;
        virtualKey.CommandText = """
            INSERT INTO "VirtualKeys" ("KeyName","KeyHash","Description","IsEnabled","VirtualKeyGroupId","RateLimitRpm","RateLimitRpd","CreatedAt","UpdatedAt")
            VALUES (@owner,@hash,'Responses accounting proof',true,@group,100,1000,@now,@now) RETURNING "Id"
            """;
        virtualKey.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, owner);
        virtualKey.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant());
        virtualKey.Parameters.AddWithValue("group", NpgsqlDbType.Integer, groupId);
        virtualKey.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, DateTime.UtcNow);
        var keyId = (int)(await virtualKey.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("No fixture key."));
        await transaction.CommitAsync(token);
        return (groupId, keyId);
    }

    private static async Task AssertDurableAsync(NpgsqlDataSource source, int groupId, int keyId, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await using var command = source.CreateCommand("""
                SELECT (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId"=@key),
                  (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId"=@key AND "RequestType"='responses'
                   AND "ModelName"='native-aot-model' AND "InputTokens"=10 AND "OutputTokens"=5
                   AND "Cost"=0.000075 AND "BilledAtUtc" IS NOT NULL),
                  (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId"=@key AND "StatusCode"=502),
                  (SELECT coalesce(sum("Cost"),0) FROM "RequestLogs" WHERE "VirtualKeyId"=@key),
                  g."Balance",g."LifetimeSpent",
                  (SELECT coalesce(sum("Amount"),0) FROM "VirtualKeyGroupTransactions"
                   WHERE "VirtualKeyGroupId"=@group AND "TransactionType"=2 AND NOT "IsDeleted"),
                  (SELECT count(*)-count(DISTINCT "IdempotencyKey") FROM "VirtualKeyGroupTransactions"
                   WHERE "VirtualKeyGroupId"=@group AND "IdempotencyKey" IS NOT NULL),
                  (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId"=@key AND "StatusCode"=200)
                FROM "VirtualKeyGroups" g WHERE g."Id"=@group
                """);
            command.Parameters.AddWithValue("key", NpgsqlDbType.Integer, keyId);
            command.Parameters.AddWithValue("group", NpgsqlDbType.Integer, groupId);
            await using var reader = await command.ExecuteReaderAsync(token);
            Require(await reader.ReadAsync(token), "Owned Responses proof group disappeared.");
            Require(reader.GetInt64(0) <= 3 && reader.GetInt64(7) == 0, "Duplicate request audit or ledger charge.");
            if (reader.GetInt64(0) == 3 && reader.GetInt64(1) == 3 && reader.GetInt64(2) == 2 && reader.GetInt64(8) == 1 &&
                reader.GetDecimal(3) == 3 * Price && reader.GetDecimal(4) == 100 - 3 * Price &&
                reader.GetDecimal(5) == 3 * Price && reader.GetDecimal(6) == 3 * Price) return;
            await Task.Delay(250, token);
        }
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
