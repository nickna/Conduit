using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace ConduitLLM.Configuration.Messaging
{
    /// <summary>
    /// Backend-neutral DI helpers for the messaging abstraction (epic #909). These register
    /// the domain-facing <see cref="IEventHandler{TEvent}"/> implementations on the service
    /// collection independently of the hosting bus; the active backend (Wolverine) binds a
    /// generic bridge that dispatches to every registered handler.
    /// </summary>
    public static class MessagingServiceCollectionExtensions
    {
        /// <summary>
        /// Registers a handler implementation for an event type. A single class may be
        /// registered for several event types (call once per type it handles).
        /// </summary>
        public static IServiceCollection AddEventHandler<TEvent, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IServiceCollection services)
            where TEvent : class
            where THandler : class, IEventHandler<TEvent>
        {
            services.AddScoped<IEventHandler<TEvent>, THandler>();
            return services;
        }
    }
}
