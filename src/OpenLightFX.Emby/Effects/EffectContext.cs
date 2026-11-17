namespace OpenLightFX.Emby.Effects;

using OpenLightFX.Emby.Drivers;

/// <summary>
/// Scene colour a lightning afterglow should approach. Brightness may be 0;
/// the renderer keeps the last step lit so a rate-limited bulb is not powered off.
/// </summary>
public readonly record struct EffectSceneColor(byte R, byte G, byte B, uint Brightness);

/// <summary>
/// Context provided to effect renderers for capability-aware rendering.
/// </summary>
public record EffectContext(
    BulbCapabilityProfile BulbCapabilities,
    uint GlobalBrightnessCap,
    bool PhotosensitivityEnabled,
    EffectSceneColor? Scene = null
);
