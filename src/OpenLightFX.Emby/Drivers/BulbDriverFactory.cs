namespace OpenLightFX.Emby.Drivers;

using OpenLightFX.Emby.Models;

public static class BulbDriverFactory
{
    public static IBulbDriver Create(BulbConfig config)
    {
        return config switch
        {
            WizBulbConfig   wiz   => new Wiz.WizDriver(wiz),
            HueBulbConfig   hue   => new Hue.HueDriver(hue),
            LifxBulbConfig  lifx  => new Lifx.LifxDriver(lifx),
            GoveeBulbConfig govee => new Govee.GoveeDriver(govee),
            RestBulbConfig  rest  => new GenericRest.GenericRestDriver(rest),
            _ => throw new ArgumentException($"Unknown bulb config type: {config.GetType().Name}")
        };
    }
}
