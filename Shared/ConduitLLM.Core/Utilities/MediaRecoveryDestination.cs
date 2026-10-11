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
        {
            if ((b[0] & 0xe0) != 0x20) return false;
            // Documentation and IPv4 transition destinations are not recovery endpoints.
            if (b is [0x20, 1, 0, 0, ..] or [0x20, 1, 0x0d, 0xb8, ..] or [0x20, 2, ..]) return false;
            return true;
        }
        return b switch
        {
            [0 or 10 or 127, ..] => false,
            [>= 224, ..] => false,
            [100, >= 64 and <= 127, ..] => false,
            [169, 254, ..] => false,
            [172, >= 16 and <= 31, ..] => false,
            [192, 0 or 2 or 168, ..] => false,
            [198, 18 or 19, ..] => false,
            [198, 51, 100, ..] => false,
            [203, 0, 113, ..] => false,
            _ => true
        };
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
