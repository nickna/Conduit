using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ConduitLLM.Configuration.Data
{
    /// <summary>
    /// Factory to create ConfigurationDbContext instances at design time for EF Core tools
    /// </summary>
    public class ConfigurationDbContextFactory : IDesignTimeDbContextFactory<ConduitDbContext>
    {
        /// <summary>
        /// Creates a new ConfigurationDbContext for design-time operations
        /// </summary>
        public ConduitDbContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<ConduitDbContext>();
            optionsBuilder.UseNpgsql(ResolveNpgsqlConnectionString());

            return new ConduitDbContext(optionsBuilder.Options);
        }

        /// <summary>
        /// Resolves the Npgsql connection string from the DATABASE_URL environment
        /// variable, accepting postgres://, postgresql://, or Npgsql key=value form.
        /// Shared by design-time tooling and the "migrate" CLI verb.
        /// </summary>
        public static string ResolveNpgsqlConnectionString()
        {
            // Read connection string from environment variable
            var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException(
                    "DATABASE_URL environment variable is not set. PostgreSQL is required for Conduit.\n" +
                    "Set DATABASE_URL to a valid PostgreSQL connection string:\n" +
                    "Example: postgresql://user:password@localhost:5432/conduitdb");
            }

            return PostgresConnectionStringParser.Parse(connectionString).ConnectionString;
        }
    }
}
