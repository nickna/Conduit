using JasperFx;
using JasperFx.CodeGeneration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using System.Reflection;

using Wolverine;
using Wolverine.Postgresql;
using Wolverine.ErrorHandling;
using ConduitLLM.Core.Caching;

namespace ConduitLLM.Configuration.Messaging.Wolverine
{
    /// <summary>
    /// Bootstrap for the Wolverine backend of the messaging abstraction (Phase 2 of
    /// epic #909, I2.1/#924). Configures the Wolverine host on the PostgreSQL
    /// transport with durable message persistence, reusing the service's existing
    /// Npgsql database — no new broker.
    /// </summary>
    /// <remarks>
    /// This registers the Wolverine host, the active messaging backend on the
    /// PostgreSQL transport. The <see cref="IEventBus"/> adapter and the
    /// <see cref="IEventHandler{TEvent}"/> handler host are wired up alongside it
    /// (I2.2/#925).
    /// </remarks>
    public static class WolverineMessagingExtensions
    {
        /// <summary>
        /// Configuration key for the durability schema name. Default is per-service
        /// (<c>wolverine_conduit_gateway</c> / <c>wolverine_conduit_admin</c>): the node,
        /// agent-assignment, and envelope tables MUST NOT be shared between services, or
        /// the two hosts form a single agent cluster and the leader (which may be the
        /// Admin) fails to assign the Gateway-only exclusive strict-ordering listeners
        /// (spend-update-events / image-generation-events) — leaving those queues with
        /// no consumer.
        /// </summary>
        public const string SchemaNameKey = "ConduitLLM:Messaging:Wolverine:SchemaName";

        /// <summary>
        /// Schema holding the PostgreSQL transport queue tables. Shared by all services —
        /// cross-service delivery (Admin -&gt; Gateway) works by writing to the same queue
        /// tables, so this schema must stay common even though durability schemas are
        /// per-service.
        /// </summary>
        public const string TransportSchemaName = "wolverine_queues";

        /// <summary>
        /// Configuration key controlling automatic provisioning of Wolverine's durability
        /// and queue tables at startup (default <c>false</c>). The explicit
        /// <c>migrate</c> release step owns provisioning. Development environments may
        /// opt in when intentionally running without that step.
        /// </summary>
        public const string AutoProvisionKey = "ConduitLLM:Messaging:Wolverine:AutoProvision";

        /// <summary>
        /// Configuration key selecting the Wolverine transport (I2.5/#928):
        /// <c>Postgresql</c> (default — durable persistence + cross-service queues) or
        /// <c>InMemory</c> (local queues only, no Postgres required — dev/CI parity with
        /// the previous backend's in-memory mode). Unrecognized values throw at boot.
        /// </summary>
        public const string TransportKey = "ConduitLLM:Messaging:Wolverine:Transport";

        /// <summary>
        /// Resolves whether the in-memory transport is selected (see <see cref="TransportKey"/>).
        /// Hosts use this to skip the Postgres queue topology, which only exists on the
        /// Postgresql transport.
        /// </summary>
        public static bool UsesInMemoryTransport(IConfiguration configuration)
        {
            var transport = configuration[TransportKey] ?? "Postgresql";

            if (transport.Equals("Postgresql", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (transport.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            throw new InvalidOperationException(
                $"Unrecognized value '{transport}' for '{TransportKey}'. Valid values: Postgresql, InMemory.");
        }

        /// <summary>
        /// Adds the Wolverine host on the PostgreSQL transport with durable persistence.
        /// </summary>
        /// <param name="host">The host builder.</param>
        /// <param name="configuration">App configuration (schema/provisioning knobs).</param>
        /// <param name="connectionString">
        /// The service's PostgreSQL connection string (the same database EF Core uses,
        /// resolved per-service, e.g. "CoreAPI" / "AdminAPI").
        /// </param>
        /// <param name="serviceName">
        /// Wolverine service identity (e.g. <c>conduit-gateway</c>); distinguishes each
        /// service's durability agent and node records in the shared database.
        /// </param>
        /// <param name="configure">
        /// Per-service Wolverine configuration applied after the Conduit defaults —
        /// bridge registrations (<see cref="AddEventBridge{TEvent}"/>) and, from
        /// I2.3/#926, the tuned endpoint policies.
        /// </param>
        public static IHostBuilder AddConduitWolverine(
            this IHostBuilder host,
            IConfiguration configuration,
            string connectionString,
            string serviceName,
            Action<WolverineOptions>? configure = null)
        {
            var schemaName = configuration[SchemaNameKey]
                ?? $"wolverine_{serviceName.Replace('-', '_')}";
            var autoProvision = configuration.GetValue(AutoProvisionKey, false);
            var inMemory = UsesInMemoryTransport(configuration);

#if CONDUIT_NATIVE_AOT
            // JasperFx otherwise probes the entry assembly's references and walks the
            // managed stack while AddWolverine registers its shared defaults. Neither
            // reflection path exists in a NativeAOT image. Pinning the application
            // assembly is JasperFx's public escape hatch and leaves JIT discovery intact.
            JasperFxOptions.RememberedApplicationAssembly =
                Assembly.GetEntryAssembly() ?? typeof(WolverineMessagingExtensions).Assembly;
#endif

            return host.UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;

                // Handler adapters are committed under each host's Internal/Generated
                // directory. Static mode is deliberately unconditional: every environment
                // fails fast when generated code is missing instead of falling back to
                // runtime Roslyn compilation.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;

                if (inMemory)
                {
                    // In-memory transport (I2.5/#928): local queues only, no persistence —
                    // dev/CI can run the Wolverine backend without Postgres, matching the
                    // durability level of the previous backend's in-memory mode. Solo skips the
                    // multi-node leader-election agents a single test host never needs.
                    opts.Durability.Mode = DurabilityMode.Solo;
                }
                else
                {
                    // Persistence (inbox/outbox/scheduled messages) AND the message
                    // transport share the existing Postgres database. The durability
                    // schema is per-service (see SchemaNameKey) so each host runs its own
                    // agent cluster; the transport schema is shared so cross-service
                    // queues (Admin -> Gateway) keep working.
                    opts.UsePostgresqlPersistenceAndTransport(
                        connectionString, schemaName, transportSchema: TransportSchemaName);

                    // Local queues (where in-process bridge handlers receive publishes) are
                    // backed by the Postgres durability tables, so buffered messages survive
                    // a crash — already an improvement on the previous backend's in-memory transport.
                    opts.Policies.UseDurableLocalQueues();

                    // Transactional outbox (I2.4/#927): every sending endpoint persists the
                    // envelope to the Postgres outbox before delivery, so a publish accepted
                    // by the bus survives a crash and is retried by the durability agent —
                    // the fire-and-forget publish seams no longer lose events on transient
                    // failure. Messages published from inside a handler additionally flush
                    // atomically with handler completion (the message-context outbox).
                    opts.Policies.UseDurableOutboxOnAllSendingEndpoints();

                    opts.AutoBuildMessageStorageOnStartup = autoProvision
                        ? AutoCreate.CreateOrUpdate
                        : AutoCreate.None;

                    // The durability agent's periodic node-assignment checks would
                    // otherwise emit a steady stream of no-op spans (#931).
                    opts.Durability.NodeAssignmentHealthCheckTracingEnabled = false;
                }

                // Wraps handlers in a database transaction where one applies.
                opts.Policies.AutoApplyTransactions();

                // Conduit's IEventHandler<T> implementations are named *Handler/*Consumer
                // with HandleAsync methods, which Wolverine's conventional discovery would
                // otherwise pick up as native handlers (with IEventContext unresolvable).
                // Dispatch goes exclusively through the explicit bridge registrations
                // added in I2.2/#925.
                opts.Discovery.DisableConventionalDiscovery();

                // Acknowledging a failed invalidation leaves every other cache node stale.
                // Keep these idempotent operations durable across an extended Redis outage.
                opts.Policies.OnException<ApplicationCacheInvalidationException>()
                    .ScheduleRetryIndefinitely(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

                configure?.Invoke(opts);
            }, ExtensionDiscovery.ManualOnly);
        }

        /// <summary>
        /// Registers the <see cref="IEventBus"/> adapter over Wolverine's
        /// <see cref="IMessageBus"/>. Scoped for the same reason as the previous
        /// backend's event-bus registration: inside a handler scope the bus is the active
        /// message context, so follow-on publishes stay correlation-aware.
        /// </summary>
        public static IServiceCollection AddWolverineEventBus(this IServiceCollection services)
        {
            services.AddScoped<IEventBus, WolverineEventBus>();
            return services;
        }

        /// <summary>
        /// Registers the generic Wolverine bridge handler for an event type — the
        /// Wolverine analogue of the previous backend's <c>AddEventBridge</c>. Causes the event
        /// type to be consumed and dispatched to every registered
        /// <see cref="IEventHandler{TEvent}"/>. Required because conventional discovery
        /// is disabled.
        /// </summary>
        public static void AddEventBridge<TEvent>(this WolverineOptions options)
            where TEvent : class
        {
            options.Discovery.IncludeType<WolverineHandlerBridge<TEvent>>();

            // The generated bridge adapter must resolve the scoped handler collection per
            // message. Some handlers behind that collection use opaque lambda factories,
            // typed clients, or IServiceScopeFactory, so Wolverine cannot safely inline
            // their complete construction graph. Keep the global service-location policy
            // at its strict default and opt in only this explicitly registered collection.
            options.CodeGeneration.AlwaysUseServiceLocationFor<IEnumerable<IEventHandler<TEvent>>>();
            options.Services.AddScoped<WolverineHandlerBridge<TEvent>>();
        }
    }
}
