namespace OpenLightFX.Emby.Drivers;

public record BulbCapabilityProfile(
    bool SupportsRgb,
    bool SupportsColorTemp,
    uint ColorTempMin,
    uint ColorTempMax,
    uint MinTransitionMs,
    uint MaxCommandsPerSecond)
{
    public bool HasFastTransition => MinTransitionMs <= 100;

    public bool MeetsCapability(string? requiredCapability) => requiredCapability switch
    {
        null or "" or "CAPABILITY_ANY" => true,
        "CAPABILITY_FAST_TRANSITION" => HasFastTransition,
        "CAPABILITY_RGB" => SupportsRgb,
        "CAPABILITY_FAST_RGB" => SupportsRgb && HasFastTransition,
        _ => false
    };
}
