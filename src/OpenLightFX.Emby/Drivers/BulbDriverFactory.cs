namespace OpenLightFX.Emby.Drivers;

using OpenLightFX.Emby.Models;

public static class BulbDriverFactory
{
    public static IBulbDriver Create(BulbConfig config)
    {
        return config.Protocol.ToLowerInvariant() switch
        {
            "wiz" => new Wiz.WizDriver(config),
            "hue" => new Hue.HueDriver(config),
            "lifx" => new Lifx.LifxDriver(config),
            "govee" => new Govee.GoveeDriver(config),
            "rest" => new GenericRest.GenericRestDriver(config),
            _ => throw new ArgumentException($"Unknown bulb protocol: {config.Protocol}")
        };
    }
}
