using Aion.Host.Platform;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlatformSynapseTests
{
    static readonly byte[] EmptyObject = "{}"u8.ToArray();

    [Theory]
    [InlineData("GET", "/wyvrn/Synapse")]
    [InlineData("GET", "/wyvrn/Synapse/")]
    [InlineData("POST", "/wyvrn/Synapse/v1")]
    public void Synapse_on_bootstrap_port_returns_empty_json(string method, string path)
    {
        Assert.True(PlatformSynapseResponses.TryReply(method, path, out var body));
        Assert.Equal(EmptyObject, body);
    }
}
