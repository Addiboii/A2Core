using System.Net;
using System.Net.Sockets;

namespace Aion.Host;

/// <summary>
/// Where the host listens and the address it hands to clients (Aion:Network). Defaults keep the
/// development host on loopback; a public server sets BindAddress (e.g. 0.0.0.0) and PublicAddress
/// (the IPv4 address players reach). The login redirect (0x390f) carries the address as 7..15 ASCII
/// characters and LoginServerIP is "ip:port", so PublicAddress must be an IPv4 literal.
/// </summary>
public sealed record NetworkOptions
{
    public string BindAddress { get; init; } = "127.0.0.1";
    public string PublicAddress { get; init; } = "127.0.0.1";

    public IPAddress Bind => IPAddress.TryParse(BindAddress, out var address)
        ? address : throw new InvalidDataException("Aion:Network:BindAddress must be an IP address");

    /// <summary>Loopback-only host: native listeners then also refuse non-loopback peers.</summary>
    public bool LoopbackOnly => IPAddress.IsLoopback(Bind);

    public void Validate()
    {
        _ = Bind;
        if (!IPAddress.TryParse(PublicAddress, out var advertised) || advertised.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidDataException("Aion:Network:PublicAddress must be an IPv4 address");
        if (!LoopbackOnly && IPAddress.IsLoopback(advertised))
            throw new InvalidDataException("A public bind address needs Aion:Network:PublicAddress (the address players connect to)");
    }
}
