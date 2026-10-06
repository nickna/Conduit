using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Wolverine;

using Xunit;

namespace ConduitLLM.Tests.Messaging
{
    /// <summary>
    /// Contract for the messaging abstraction (originally the cross-backend contract of
    /// I2.5/#928; the second backend's half was removed with that backend in I3.1/#932). The
    /// assertions pin the behavior of the <see cref="IEventBus"/>/<see cref="IEventHandler{TEvent}"/>
    /// implementation on the Wolverine backend (in-memory local queues). The abstract base is
    /// kept so a future backend can be validated against the identical contract; concrete
    /// subclasses supply only the host bootstrap.
    /// </summary>
    public abstract class EventBusBackendContractTests : IAsyncLifetime
    {
        public record ContractEvent(string Payload);
        public record FollowOnEvent(string Payload);

        public sealed class ContractSink
        {
            public ConcurrentQueue<ContractEvent> Primary { get; } = new();
            public ConcurrentQueue<ContractEvent> Secondary { get; } = new();
            public ConcurrentQueue<FollowOnEvent> FollowOns { get; } = new();
            public Guid? LastMessageId { get; set; }
            public string? LastCorrelationId { get; set; }

            public TaskCompletionSource<bool> PrimarySignal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> SecondarySignal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> FollowOnSignal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private int _expectedPrimary = 1;
            public void ExpectPrimary(int count) => _expectedPrimary = count;

            public void RecordPrimary(ContractEvent @event, IEventContext context)
            {
                Primary.Enqueue(@event);
                LastMessageId = context.MessageId;
                LastCorrelationId = context.CorrelationId;
                if (Primary.Count >= _expectedPrimary)
                {
                    PrimarySignal.TrySetResult(true);
                }
            }
        }

        public sealed class PrimaryHandler : IEventHandler<ContractEvent>
        {
            private readonly ContractSink _sink;
            public PrimaryHandler(ContractSink sink) => _sink = sink;

            public Task HandleAsync(ContractEvent @event, IEventContext context)
            {
                _sink.RecordPrimary(@event, context);
                return Task.CompletedTask;
            }
        }

        public sealed class SecondaryHandler : IEventHandler<ContractEvent>
        {
            private readonly ContractSink _sink;
            public SecondaryHandler(ContractSink sink) => _sink = sink;

            public Task HandleAsync(ContractEvent @event, IEventContext context)
            {
                _sink.Secondary.Enqueue(@event);
                _sink.SecondarySignal.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        /// <summary>Publishes a follow-on event through the delivery context.</summary>
        public sealed class CascadingHandler : IEventHandler<ContractEvent>
        {
            public async Task HandleAsync(ContractEvent @event, IEventContext context)
            {
                await context.PublishAsync(new FollowOnEvent(@event.Payload + "-cascaded"));
            }
        }

        public sealed class FollowOnHandler : IEventHandler<FollowOnEvent>
        {
            private readonly ContractSink _sink;
            public FollowOnHandler(ContractSink sink) => _sink = sink;

            public Task HandleAsync(FollowOnEvent @event, IEventContext context)
            {
                _sink.FollowOns.Enqueue(@event);
                _sink.FollowOnSignal.TrySetResult(true);
                return Task.CompletedTask;
            }
        }

        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        protected ContractSink Sink { get; } = new();

        /// <summary>
        /// Boots the backend under test with the given handler registrations and one
        /// bridge per event type. The sink is pre-registered.
        /// </summary>
        protected abstract Task StartHostAsync(
            Action<IServiceCollection> registerHandlers,
            params Type[] bridgedEventTypes);

        /// <summary>Root provider of the running host.</summary>
        protected abstract IServiceProvider Services { get; }

        protected abstract Task StopHostAsync();

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => StopHostAsync();

        [Fact]
        public async Task Publish_DispatchesToHandler_WithEnvelopeMetadata()
        {
            await StartHostAsync(
                services => services.AddScoped<IEventHandler<ContractEvent>, PrimaryHandler>(),
                typeof(ContractEvent));

            using (var scope = Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await bus.PublishAsync(new ContractEvent("hello"));
            }

            await Sink.PrimarySignal.Task.WaitAsync(Timeout);
            Sink.Primary.Should().ContainSingle().Which.Payload.Should().Be("hello");
            Sink.LastMessageId.Should().NotBeNull();
            // CorrelationId is deliberately NOT asserted for a bare publish: Wolverine
            // auto-generates one, and Conduit's DomainEvents carry their own when it matters.
        }

        [Fact]
        public async Task Publish_DispatchesToAllRegisteredHandlers()
        {
            await StartHostAsync(
                services =>
                {
                    services.AddScoped<IEventHandler<ContractEvent>, PrimaryHandler>();
                    services.AddScoped<IEventHandler<ContractEvent>, SecondaryHandler>();
                },
                typeof(ContractEvent));

            using (var scope = Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await bus.PublishAsync(new ContractEvent("fan-out"));
            }

            await Sink.PrimarySignal.Task.WaitAsync(Timeout);
            await Sink.SecondarySignal.Task.WaitAsync(Timeout);
            Sink.Primary.Should().ContainSingle();
            Sink.Secondary.Should().ContainSingle();
        }

        [Fact]
        public async Task PublishBatch_DispatchesEveryEvent()
        {
            Sink.ExpectPrimary(3);

            await StartHostAsync(
                services => services.AddScoped<IEventHandler<ContractEvent>, PrimaryHandler>(),
                typeof(ContractEvent));

            using (var scope = Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await bus.PublishBatchAsync(new List<ContractEvent>
                {
                    new("one"), new("two"), new("three")
                });
            }

            await Sink.PrimarySignal.Task.WaitAsync(Timeout);
            Sink.Primary.Should().HaveCount(3);
        }

        [Fact]
        public async Task HandlerCascade_PublishesFollowOnEvent_ThroughContext()
        {
            await StartHostAsync(
                services =>
                {
                    services.AddScoped<IEventHandler<ContractEvent>, CascadingHandler>();
                    services.AddScoped<IEventHandler<FollowOnEvent>, FollowOnHandler>();
                },
                typeof(ContractEvent), typeof(FollowOnEvent));

            using (var scope = Services.CreateScope())
            {
                var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                await bus.PublishAsync(new ContractEvent("origin"));
            }

            await Sink.FollowOnSignal.Task.WaitAsync(Timeout);
            Sink.FollowOns.Should().ContainSingle().Which.Payload.Should().Be("origin-cascaded");
        }
    }

    /// <summary>Wolverine backend run of the contract (in-memory local queues).</summary>
    public class WolverineEventBusContractTests : EventBusBackendContractTests
    {
        private IHost? _host;

        protected override IServiceProvider Services =>
            _host?.Services ?? throw new InvalidOperationException("Host not started");

        protected override async Task StartHostAsync(
            Action<IServiceCollection> registerHandlers,
            params Type[] bridgedEventTypes)
        {
            _host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Production parity with AddConduitWolverine, minus Postgres.
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.UseRuntimeCompilation();

                    if (bridgedEventTypes.Contains(typeof(ContractEvent)))
                        opts.AddEventBridge<ContractEvent>();
                    if (bridgedEventTypes.Contains(typeof(FollowOnEvent)))
                        opts.AddEventBridge<FollowOnEvent>();

                    opts.Services.AddSingleton(Sink);
                    registerHandlers(opts.Services);
                    opts.Services.AddWolverineEventBus();
                })
                .StartAsync();
        }

        protected override async Task StopHostAsync()
        {
            if (_host != null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
        }
    }
}
