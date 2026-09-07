using System.Globalization;
using System.Net;

namespace PalworldServerManager.Contracts;

// Reachability only. Neither this address nor DNS resolution establishes a Host identity.
public sealed record HostReachableAddress
{
    public string Host { get; }
    public int Port { get; }
    public Uri HttpsAddress { get; }
    private HostReachableAddress(string host, int port)
    { Host = host; Port = port; HttpsAddress = new UriBuilder("https", host, port).Uri; }

    public static HostReachableAddress Parse(string host, int port)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (port is < 1 or > 65535 || host.Length is 0 or > 253 || host != host.Trim() ||
            host.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || host.IndexOfAny(['/', '\\', '@', '?', '#', '%']) >= 0)
            throw Invalid();
        var bracketed = host.StartsWith('[') && host.EndsWith(']');
        if (bracketed) host = host[1..^1];
        if (IPAddress.TryParse(host, out var ip))
        {
            if (bracketed && ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) throw Invalid();
            return FromIp(ip, port);
        }
        if (bracketed || host.IndexOfAny([':', '%', '[', ']']) >= 0) throw Invalid();
        string ascii;
        try { ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException) { throw Invalid(); }
        if (ascii.EndsWith('.')) ascii = ascii[..^1];
        if (ascii.Length is 0 or > 253 || ascii.Split('.').Any(label => label.Length is 0 or > 63 ||
            !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]) ||
            label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))) throw Invalid();
        // IDNA normalization can produce an IP literal too; apply the same endpoint checks.
        return IPAddress.TryParse(ascii, out ip) ? FromIp(ip, port) : new(ascii, port);
    }
    private static HostReachableAddress FromIp(IPAddress ip, int port)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast) || ip.IsIPv6Multicast ||
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0) ||
            (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224))) throw Invalid();
        return new(ip.ToString(), port);
    }
    private static ArgumentException Invalid() => new("Enter a reachable hostname or IP and a separate port.");
}
