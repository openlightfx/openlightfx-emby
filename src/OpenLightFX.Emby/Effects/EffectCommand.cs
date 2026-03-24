namespace OpenLightFX.Emby.Effects;

/// <summary>
/// A single bulb command produced by an effect renderer at a specific time offset.
/// </summary>
public record EffectCommand(
    uint OffsetMs,
    byte R, byte G, byte B,
    uint Brightness,
    uint TransitionMs
);
