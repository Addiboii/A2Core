namespace Aion.Host.Debug;

/// <summary>Razer Chroma Synapse polling stub (the client calls /wyvrn/Synapse on loopback).</summary>
public static class SynapseEndpoints
{
    public static void MapSynapseStub(this WebApplication app)
    {
        app.MapMethods("/wyvrn/Synapse/{**rest}", ["GET", "POST", "HEAD"], () => Results.Ok(new { ok = true }));
    }
}
