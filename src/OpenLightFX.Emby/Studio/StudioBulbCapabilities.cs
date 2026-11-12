using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Models;

namespace OpenLightFX.Emby.Studio;

public static class StudioBulbCapabilities
{
    public static BulbCapabilityProfile ApplyOverrides(BulbCapabilityProfile caps, CapabilityOverrides? overrides)
    {
        if (overrides == null)
            return caps;

        return caps with
        {
            SupportsRgb = overrides.SupportsRgb ?? caps.SupportsRgb,
            SupportsColorTemp = overrides.SupportsColorTemp ?? caps.SupportsColorTemp,
            MinTransitionMs = overrides.MinTransitionMs ?? caps.MinTransitionMs,
            MaxCommandsPerSecond = overrides.MaxCommandsPerSecond ?? caps.MaxCommandsPerSecond
        };
    }

    /// <summary>
    /// Protocol defaults used when a driver cannot be constructed. Values match the drivers.
    /// </summary>
    public static BulbCapabilityProfile ForProtocol(BulbProtocol protocol) => protocol switch
    {
        BulbProtocol.Wiz => new BulbCapabilityProfile(true, true, 2200, 6500, 100, 5),
        BulbProtocol.Hue => new BulbCapabilityProfile(true, true, 2000, 6535, 100, 10),
        BulbProtocol.Lifx => new BulbCapabilityProfile(true, true, 1500, 9000, 50, 20),
        BulbProtocol.Govee => new BulbCapabilityProfile(true, true, 2000, 9000, 100, 5),
        BulbProtocol.Rest => new BulbCapabilityProfile(true, false, 2000, 6500, 200, 5),
        _ => new BulbCapabilityProfile(false, false, 2000, 6500, 200, 1)
    };

    public static async Task<BulbCapabilityProfile> ReadAsync(BulbConfig config)
    {
        try
        {
            await using var driver = BulbDriverFactory.Create(config);
            return ApplyOverrides(driver.GetCapabilities(), config.CapabilityOverrides);
        }
        catch
        {
            return ApplyOverrides(ForProtocol(config.Protocol), config.CapabilityOverrides);
        }
    }
}
