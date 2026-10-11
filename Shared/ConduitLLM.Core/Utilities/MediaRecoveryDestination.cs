using System.Net;
using System.Net.Sockets;

namespace ConduitLLM.Core.Utilities;

/// <summary>Public HTTPS destinations, resolved and pinned at connection time to prevent DNS rebinding.</summary>
public static class MediaRecoveryDestination
{
    public const string ClientName = "MediaRecoveryDownload";
    public static bool IsValidUrl(string? value) => value?.Length <= 2048 &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Port == 443 &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !uri.IsLoopback &&
        (!IPAddress.TryParse(uri.Host, out var ip) || IsPublicAddress(ip));

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (b[0] & 0xe0) == 0x20 &&
                !(b[0] == 0x20 && b[1] == 1 && (b[2] == 0 && b[3] == 0 || b[2] == 0x0d && b[3] == 0xb8)) &&
                !(b[0] == 0x20 && b[1] == 2); // Exclude Teredo and 6to4 transition destinations.
        return b[0] is not (0 or 10 or 127) && b[0] < 224 &&
            !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && (b[1] == 168 || b[1] == 0 || b[1] == 2)) &&
            !(b[0] == 198 && b[1] is 18 or 19 || b[0] == 198 && b[1] == 51 && b[2] == 100) &&
            !(b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (context.DnsEndPoint.Port != 443) throw new HttpRequestException("Recovery requires HTTPS.");
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a)))
            throw new HttpRequestException("Recovery destination is not public.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], 443), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
