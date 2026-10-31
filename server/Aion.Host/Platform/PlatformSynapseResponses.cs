namespace Aion.Host.Platform;

/// <summary>Razer Chroma / Wyvrn Synapse polling on the platform bootstrap port (GET /wyvrn/Synapse).</summary>
public static class PlatformSynapseResponses
{
    public static bool TryReply(string method, string path, out byte[] body)
    {
        body = [];
        if (!path.StartsWith("/wyvrn/Synapse", StringComparison.OrdinalIgnoreCase)) return false;
        if (method is not ("GET" or "POST" or "HEAD")) return false;
        body = "{}"u8.ToArray();
        return true;
    }
}
