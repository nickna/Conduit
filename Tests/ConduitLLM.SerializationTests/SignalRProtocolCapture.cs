using System.Buffers;
using System.Text;
using System.Text.Json;
using ConduitLLM.Configuration.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;

namespace ConduitLLM.SerializationTests;

internal sealed class SignalRProtocolCapture<THub> : IHubContext<THub>, IHubClients where THub : Hub
{
    private readonly JsonHubProtocol _protocol;

    public SignalRProtocolCapture(JsonSerializerOptions? options = null)
    {
        _protocol = new JsonHubProtocol(Options.Create(new JsonHubProtocolOptions
        {
            PayloadSerializerOptions = options ?? new JsonSerializerOptions(ConfigurationSignalRJsonContext.Default.Options)
        }));
    }

    public List<CapturedInvocation> Invocations { get; } = [];
    public List<Exception> Errors { get; } = [];
    public IHubClients Clients => this;
    public IGroupManager Groups => throw new NotSupportedException();
    public IClientProxy All => Group("all");
    public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
    public IClientProxy Client(string connectionId) => throw new NotSupportedException();
    IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
    public IClientProxy Group(string groupName) => new CapturingProxy(this, groupName);
    public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
    IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
    public IClientProxy User(string userId) => throw new NotSupportedException();
    public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();

    private sealed class CapturingProxy(SignalRProtocolCapture<THub> owner, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            try
            {
                var buffer = new ArrayBufferWriter<byte>();
                owner._protocol.WriteMessage(new InvocationMessage(method, args), buffer);
                var frame = Encoding.UTF8.GetString(buffer.WrittenSpan);
                Assert.EndsWith("\u001e", frame);
                using var document = JsonDocument.Parse(frame[..^1]);
                var root = document.RootElement;
                Assert.Equal(1, root.GetProperty("type").GetInt32());
                Assert.Equal(method, root.GetProperty("target").GetString());
                Assert.Equal(1, root.GetProperty("arguments").GetArrayLength());
                owner.Invocations.Add(new(group, method, args[0]!, root.GetProperty("arguments")[0].Clone()));
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                owner.Errors.Add(exception);
                throw;
            }
        }
    }
}

internal sealed record CapturedInvocation(string Group, string Method, object Payload, JsonElement Json);
