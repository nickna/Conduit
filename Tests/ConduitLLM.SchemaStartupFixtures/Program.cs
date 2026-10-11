using System.Text.Json.Nodes;

using ConduitLLM.Configuration.Data;
namespace ConduitLLM.SchemaStartupFixtures;

internal static class StartupFixtureProgram
{
    public static async Task Main(string[] args)
    {
        var management = Environment.GetEnvironmentVariable("CONDUIT_NATIVE_STARTUP_MANAGEMENT_CONNECTION_STRING")
                         ?? throw new InvalidOperationException("CONDUIT_NATIVE_STARTUP_MANAGEMENT_CONNECTION_STRING is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
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
            throw new ArgumentException("Usage: create missing|stale, or drop <fixture-generated database name>.");
        }
    }
}
