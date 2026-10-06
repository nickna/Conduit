using System;

namespace ConduitLLM.Core.Options
{
    /// <summary>
    /// Configuration options for coordinated connection pool warming.
    /// Enables multiple instances to coordinate their pool warming to prevent
    /// thundering herd effects during deployments.
    /// </summary>
    public class ConnectionPoolWarmingOptions
    {
        /// <summary>
        /// Configuration section name in appsettings.json.
        /// </summary>
        public const string SectionName = "ConduitLLM:ConnectionPoolWarming";

        /// <summary>
        /// Gets or sets whether coordinated warming is enabled.
        /// When true, instances coordinate via distributed lock and pub/sub.
        /// When false, each instance warms immediately without coordination.
        /// Default: true
        /// </summary>
        public bool EnableCoordinatedWarming { get; set; } = true;

        /// <summary>
        /// Gets or sets how long follower instances wait for the warming signal
        /// before timing out and warming their own pools.
        /// Default: 2 minutes
        /// </summary>
        public TimeSpan SignalTimeout { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// Gets or sets the Redis channel for warming completion signals.
        /// The service type will be appended (e.g., "conduit:connectionpool:warmed:CoreAPI").
        /// Default: "conduit:connectionpool:warmed"
        /// </summary>
        public string WarmingSignalChannel { get; set; } = "conduit:connectionpool:warmed";

        /// <summary>
        /// Gets or sets the distributed lock key for warming coordination.
        /// The service type will be appended (e.g., "conduit:connectionpool:warming:CoreAPI").
        /// Default: "conduit:connectionpool:warming"
        /// </summary>
        public string WarmingLockKey { get; set; } = "conduit:connectionpool:warming";

        /// <summary>
        /// Gets or sets the base delay after receiving signal before warming.
        /// Helps stagger warming across follower instances. Random jitter is added.
        /// Default: 500ms
        /// </summary>
        public TimeSpan StaggerDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets whether to enable verbose logging for debugging.
        /// Default: false
        /// </summary>
        public bool VerboseLogging { get; set; } = false;
    }
}
