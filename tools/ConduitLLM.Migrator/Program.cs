namespace ConduitLLM.Migrator;

public static class Program
{
    public static Task<int> Main() => MigrationRunner.RunAsync();
}
