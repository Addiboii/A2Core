using Aion.Host.Debug;

namespace Aion.Host;

/// <summary>Gameplay toggles under <c>Aion:World</c> (see docs/SERVER_CONFIGURATION.md).</summary>
public sealed record WorldOptions
{
    /// <summary>
    /// When null, defaults to <c>true</c> (companion / combat map-event AskNpc credit without client talk UI).
    /// Set <c>false</c> to require live dialog. Env: <c>Aion__World__MapEventAskNpcProximityFallback=false</c>.
    /// </summary>
    public bool? MapEventAskNpcProximityFallback { get; init; }

    public bool ResolveMapEventAskNpcProximityFallback(DebugOptions debug) =>
        MapEventAskNpcProximityFallback ?? true;
}
