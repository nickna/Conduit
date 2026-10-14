using System.Text.Json.Nodes;

using ConduitLLM.Configuration.Data;
using Microsoft.Extensions.DependencyInjection;
namespace ConduitLLM.SchemaStartupFixtures;

internal static class StartupFixtureProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["protect-function-key"])
        {
            try
            {
                var redisConnectionString = Environment.GetEnvironmentVariable("CONDUIT_NATIVE_FUNCTION_REDIS_CONNECTION_STRING")
                    ?? throw new InvalidOperationException("CONDUIT_NATIVE_FUNCTION_REDIS_CONNECTION_STRING is required.");
                var plaintext = Environment.GetEnvironmentVariable("CONDUIT_NATIVE_FUNCTION_PLAINTEXT_KEY")
                    ?? throw new InvalidOperationException("CONDUIT_NATIVE_FUNCTION_PLAINTEXT_KEY is required.");
                var services = FunctionCredentialFixture.AddServices(new ServiceCollection(), redisConnectionString);
                using var provider = services.BuildServiceProvider();
                // The caller captures this encrypted credential privately.
                Console.WriteLine(FunctionCredentialFixture.ProtectApiKey(provider, plaintext));
                return 0;
            }
            catch (Exception failure)
            {
                // Classify the failure without exposing credential-bearing messages.
                Console.Error.WriteLine($"Function credential protection failed ({failure.GetType().Name}).");
                return 1;
            }
        }

        var management = Environment.GetEnvironmentVariable("CONDUIT_NATIVE_STARTUP_MANAGEMENT_CONNECTION_STRING")
                         ?? throw new InvalidOperationException("CONDUIT_NATIVE_STARTUP_MANAGEMENT_CONNECTION_STRING is required.");
        using var deadline = new CancellationTokenSource(args is ["drop", _]
            ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(3));
        if (args is ["create", "missing" or "stale"])
        {
            var fixture = await SchemaStartupDatabase.CreateAsync(management, args[1] == "stale", deadline.Token);
            // The caller captures this credential-bearing JSON privately.
            Console.WriteLine(new JsonObject
            {
                ["name"] = fixture.Name,
                ["connectionString"] = fixture.ConnectionString,
                ["appliedVersion"] = fixture.AppliedVersion,
                ["expectedVersion"] = ConduitSchemaVersion.Current
            }.ToJsonString());
        }
        else if (args is ["drop", var name])
        {
            await SchemaStartupDatabase.DropAsync(management, name, deadline.Token);
            Console.WriteLine("Owned native startup database removed.");
        }
        else
        {
            throw new ArgumentException("Usage: create missing|stale, drop <fixture-generated database name>, or protect-function-key.");
        }
        return 0;
    }
}
