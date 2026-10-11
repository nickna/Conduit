using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;

using AwesomeAssertions;

using JasperFx.CodeGeneration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Wolverine;

using Xunit;

namespace ConduitLLM.Tests.Messaging
{
    /// <summary>
    /// Tests for the Wolverine in-memory transport mode (I2.5/#928): the PRODUCTION
    /// composition path (<see cref="WolverineMessagingExtensions.AddConduitWolverine"/>)
    /// boots and delivers events without Postgres when
    /// <c>ConduitLLM:Messaging:Wolverine:Transport = InMemory</c> — this is what lets
    /// dev/CI run the whole suite on the Wolverine backend.
    /// </summary>
    public class WolverineInMemoryTransportTests
    {
        public record InMemoryPilotEvent(string Payload);

        public sealed class PilotSink
        {
            public List<InMemoryPilotEvent> Received { get; } = new();
            public TaskCompletionSource<InMemoryPilotEvent> Signal { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public sealed class RecordingPilotHandler : IEventHandler<InMemoryPilotEvent>
        {
            private readonly PilotSink _sink;
            public RecordingPilotHandler(PilotSink sink) => _sink = sink;

            public Task HandleAsync(InMemoryPilotEvent @event, IEventContext context)
            {
                _sink.Received.Add(@event);
                _sink.Signal.TrySetResult(@event);
                return Task.CompletedTask;
            }
        }

        private static IConfiguration BuildConfiguration(string? transport)
        {
            var values = new Dictionary<string, string?>();
            if (transport != null)
            {
                values[WolverineMessagingExtensions.TransportKey] = transport;
            }

            return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        }

        [Fact]
        public void UsesInMemoryTransport_DefaultsToPostgresql()
        {
            WolverineMessagingExtensions.UsesInMemoryTransport(BuildConfiguration(null))
                .Should().BeFalse();
        }

        [Theory]
        [InlineData("Postgresql", false)]
        [InlineData("postgresql", false)]
        [InlineData("InMemory", true)]
        [InlineData("inmemory", true)]
        public void UsesInMemoryTransport_ResolvesKnownValues(string transport, bool expected)
        {
            WolverineMessagingExtensions.UsesInMemoryTransport(BuildConfiguration(transport))
                .Should().Be(expected);
        }

        [Fact]
        public void UsesInMemoryTransport_ThrowsOnUnrecognizedValue()
        {
            var act = () => WolverineMessagingExtensions.UsesInMemoryTransport(
                BuildConfiguration("RabbitMQ"));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*RabbitMQ*Postgresql, InMemory*");
        }

        [Fact]
        public async Task AddConduitWolverine_InMemoryTransport_BootsAndDeliversWithoutPostgres()
        {
            var sink = new PilotSink();
            var configuration = BuildConfiguration("InMemory");

            // The connection string is deliberately unusable: in-memory mode must never
            // touch it (Postgres persistence, transport, and outbox are all skipped).
            using var host = await Host.CreateDefaultBuilder()
                .ConfigureLogging(logging => logging.ClearProviders().AddConsole())
                .AddConduitWolverine(configuration, "Host=unreachable;Database=none", "conduit-test", opts =>
                {
                    // This isolated test declares a test-only bridge that is not part of
                    // either production host's committed adapter registry.
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                    opts.UseRuntimeCompilation();
                    opts.AddEventBridge<InMemoryPilotEvent>();
                    opts.Services.AddSingleton(sink);
                    opts.Services.AddScoped<IEventHandler<InMemoryPilotEvent>, RecordingPilotHandler>();
                    opts.Services.AddWolverineEventBus();
                })
                .StartAsync();

            try
            {
                using (var scope = host.Services.CreateScope())
                {
                    var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
                    eventBus.Should().BeOfType<WolverineEventBus>();
                    await eventBus.PublishAsync(new InMemoryPilotEvent("no-postgres"));
                }

                var received = await sink.Signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                received.Payload.Should().Be("no-postgres");
                sink.Received.Should().ContainSingle();
            }
            finally
            {
                await host.StopAsync();
            }
        }
    }
}
