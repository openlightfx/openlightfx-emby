namespace OpenLightFX.Emby.Effects;

using OpenLightFX.Emby.Drivers;

/// <summary>
/// Context provided to effect renderers for capability-aware rendering.
/// </summary>
public record EffectContext(
    BulbCapabilityProfile BulbCapabilities,
    uint GlobalBrightnessCap,
    bool PhotosensitivityEnabled
);
