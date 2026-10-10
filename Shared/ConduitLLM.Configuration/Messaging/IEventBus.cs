namespace ConduitLLM.Configuration.Messaging
{
    /// <summary>
    /// Conduit-owned abstraction for publishing domain events, independent of any
    /// specific messaging library. This is the publish half of the anti-corruption
    /// layer introduced by epic #909: domain code depends on <see cref="IEventBus"/>
    /// rather than on a third-party bus type, so the underlying transport
    /// (Wolverine) is a swappable implementation detail.
    /// </summary>
    /// <remarks>
    /// Publishing is type-routed: an event is delivered to every registered
    /// <see cref="IEventHandler{TEvent}"/> for its runtime type (and, on broker
    /// transports, to other service instances). Implementations should be safe to
    /// resolve as a singleton.
    /// </remarks>
    public interface IEventBus
    {
        /// <summary>
        /// Publishes a domain event to all interested handlers / instances.
        /// </summary>
        /// <typeparam name="TEvent">The event type. Routing is by the closed generic type.</typeparam>
        /// <param name="event">The event instance to publish. Must not be null.</param>
        /// <param name="cancellationToken">Token used to cancel the publish operation.</param>
        /// <returns>A task that completes once the event has been handed to the transport.</returns>
        Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : class;

        /// <summary>
        /// Publishes a batch of domain events of the same type in one transport operation
        /// where the backend supports it, falling back to
        /// sequential publishes otherwise.
        /// </summary>
        /// <typeparam name="TEvent">The event type. Routing is by the closed generic type.</typeparam>
        /// <param name="events">The event instances to publish. Must not be null.</param>
        /// <param name="cancellationToken">Token used to cancel the publish operation.</param>
        /// <returns>A task that completes once the batch has been handed to the transport.</returns>
        Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken cancellationToken = default)
            where TEvent : class;
    }
}
