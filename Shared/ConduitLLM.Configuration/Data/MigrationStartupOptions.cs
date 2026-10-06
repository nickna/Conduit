using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Data
{
    /// <summary>
    /// How a service participates in database schema migration at startup.
    /// </summary>
    public enum MigrationMode
    {
        /// <summary>
        /// Never migrate. Poll until the schema contains every migration this binary
        /// knows about, gating /health/ready in the meantime. This is the default;
        /// a release hook or one-shot job must run "migrate" separately.
        /// </summary>
        Wait,

        /// <summary>
        /// No migration work and no schema checks. For tests and break-glass operations only.
        /// </summary>
        Skip
    }

    /// <summary>
    /// Migration startup behavior resolved from environment variables.
    /// </summary>
    public sealed class MigrationStartupOptions
    {
        public const string ModeVariable = "CONDUIT_MIGRATION_MODE";
        public const string LockTimeoutVariable = "CONDUIT_MIGRATION_LOCK_TIMEOUT_SECONDS";
        public const string WaitTimeoutVariable = "CONDUIT_MIGRATION_WAIT_TIMEOUT_SECONDS";

        public const int DefaultLockTimeoutSeconds = 300;

        public MigrationMode Mode { get; init; } = MigrationMode.Wait;

        /// <summary>
        /// How long the explicit migrator waits for the advisory lock before failing.
        /// Only contended waiters can time out — the lock winner holds it for as long
        /// as the migration takes. 0 means wait indefinitely.
        /// </summary>
        public int LockTimeoutSeconds { get; init; } = DefaultLockTimeoutSeconds;

        /// <summary>
        /// How long a Wait-mode instance polls for the schema to become current before
        /// shutting down. 0 (default) means poll forever; readiness stays down meanwhile.
        /// </summary>
        public int WaitTimeoutSeconds { get; init; }

        public static MigrationStartupOptions FromEnvironment(ILogger logger)
        {
            WarnOnRemovedVariables(logger);

            var rawMode = Environment.GetEnvironmentVariable(ModeVariable);
            MigrationMode mode;
            if (string.IsNullOrWhiteSpace(rawMode))
            {
                mode = MigrationMode.Wait;
            }
            else if (string.Equals(rawMode.Trim(), "Apply", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{ModeVariable}=Apply is no longer supported because web services never mutate the schema. " +
                    $"Run the ConduitLLM.Migrator deployment job to migrate before rollout, " +
                    $"then use {ModeVariable}=Wait.");
            }
            else if (!Enum.TryParse(rawMode.Trim(), ignoreCase: true, out mode)
                || !Enum.IsDefined(mode))
            {
                throw new InvalidOperationException(
                    $"Unrecognized {ModeVariable} value '{rawMode}'. Valid values: Wait, Skip.");
            }

            return new MigrationStartupOptions
            {
                Mode = mode,
                LockTimeoutSeconds = ParseNonNegativeSeconds(LockTimeoutVariable, DefaultLockTimeoutSeconds),
                WaitTimeoutSeconds = ParseNonNegativeSeconds(WaitTimeoutVariable, 0)
            };
        }

        /// <summary>
        /// Options for the explicit migration command. Runtime migration mode is
        /// intentionally ignored: the command itself is the authorization to mutate
        /// the schema.
        /// </summary>
        public static MigrationStartupOptions ForMigrator(ILogger logger)
        {
            WarnOnRemovedVariables(logger);
            return new MigrationStartupOptions
            {
                Mode = MigrationMode.Wait,
                LockTimeoutSeconds = ParseNonNegativeSeconds(
                    LockTimeoutVariable,
                    DefaultLockTimeoutSeconds),
                WaitTimeoutSeconds = 0
            };
        }

        private static int ParseNonNegativeSeconds(string variable, int defaultValue)
        {
            var raw = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return defaultValue;
            }

            if (!int.TryParse(raw.Trim(), out var seconds) || seconds < 0)
            {
                throw new InvalidOperationException(
                    $"{variable} must be a non-negative integer number of seconds; got '{raw}'.");
            }

            return seconds;
        }

        private static void WarnOnRemovedVariables(ILogger logger)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CONDUIT_SKIP_DATABASE_INIT")))
            {
                logger.LogWarning(
                    "CONDUIT_SKIP_DATABASE_INIT has been REMOVED and is ignored. " +
                    "Use {ModeVariable}=Skip (tests) or {ModeVariable}=Wait (production services) instead.",
                    ModeVariable, ModeVariable);
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FORCE_RECREATE_DB_ON_FAILURE")))
            {
                logger.LogWarning(
                    "FORCE_RECREATE_DB_ON_FAILURE has been REMOVED and is ignored. " +
                    "Failed migrations now always fail startup; recreate development databases explicitly " +
                    "(e.g. ./scripts/dev.ps1 -Clean).");
            }
        }
    }
}
