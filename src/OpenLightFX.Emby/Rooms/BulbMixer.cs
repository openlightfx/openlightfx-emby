namespace OpenLightFX.Emby.Rooms;

using OpenLightFX.Emby.Engine;

/// <summary>Mixes routed channel states into one command for a physical bulb.</summary>
public static class BulbMixer
{
    public static InterpolationEngine.InterpolatedState Mix(
        IReadOnlyList<InterpolationEngine.InterpolatedState> sources)
    {
        if (sources.Count == 0) return default;
        var brightness = sources.Max(source => source.Brightness);
        ulong weight = 0;
        ulong red = 0, green = 0, blue = 0;
        foreach (var source in sources)
        {
            var amount = (ulong)source.Brightness;
            weight += amount;
            red += (ulong)source.R * amount;
            green += (ulong)source.G * amount;
            blue += (ulong)source.B * amount;
        }
        if (weight == 0)
            return new InterpolationEngine.InterpolatedState(0, 0, 0, 0, 0, Openlightfx.ColorMode.Rgb, sources.Any(source => source.PowerOn));
        return new InterpolationEngine.InterpolatedState(
            Average(red, weight),
            Average(green, weight),
            Average(blue, weight),
            brightness,
            0,
            Openlightfx.ColorMode.Rgb,
            sources.Any(source => source.PowerOn));
    }

    private static byte Average(ulong total, ulong weight) =>
        (byte)Math.Clamp((int)Math.Round((double)total / weight, MidpointRounding.ToEven), 0, 255);
}
