using System.Text.Json;
using Phase0;

try
{
    if (args is ["--provider", var port]) await Provider.RunAsync(int.Parse(port));
    else if (args is ["--run", var path])
    {
        var config = JsonSerializer.Deserialize<CaseConfig>(await File.ReadAllTextAsync(path), Wire.Json) ?? throw new InvalidDataException("Missing config");
        var report = await Client.RunAsync(config);
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(report, Wire.Json));
        Environment.ExitCode = report.Passed ? 0 : 1;
    }
    else throw new ArgumentException("Usage: --provider <loopback-port> | --run <case.json>");
}
catch (Exception exception)
{
    await Console.Error.WriteLineAsync(exception.ToString());
    Environment.ExitCode = 1;
}
