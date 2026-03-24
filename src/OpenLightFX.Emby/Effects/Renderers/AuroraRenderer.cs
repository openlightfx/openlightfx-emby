namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Slow-shifting multi-color ambient wash using sine waves for organic hue modulation.
/// </summary>
public class AuroraRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Aurora;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 5000);
        var (baseR, baseG, baseB) = GetPrimaryColor(keyframe);

        // Aurora is inherently slow — no simplification needed
        var stepMs = 250;
        var steps = (int)(duration / stepMs);
        var commands = new List<EffectCommand>(steps);

        // Convert base color to a starting hue offset
        var baseHue = RgbToHue(baseR, baseG, baseB);

        for (int i = 0; i < steps; i++)
        {
            var t = i * stepMs;
            // Three sine waves at different frequencies for organic color drift
            var hueShift = Math.Sin(2 * Math.PI * 0.1 * t / 1000.0) * 60
                         + Math.Sin(2 * Math.PI * 0.07 * t / 1000.0) * 40
                         + Math.Sin(2 * Math.PI * 0.03 * t / 1000.0) * 20;

            var satMod = 0.7 + 0.3 * Math.Sin(2 * Math.PI * 0.05 * t / 1000.0);
            var hue = (baseHue + hueShift) % 360;
            if (hue < 0) hue += 360;

            var (r, g, b) = HsvToRgb(hue, satMod, 1.0);
            var brightness = ScaleBrightness(70, intensity, cap);

            commands.Add(new EffectCommand((uint)t, r, g, b, brightness, (uint)stepMs));
        }

        return commands;
    }

    private static double RgbToHue(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var delta = max - min;

        if (delta < 0.001) return 180; // neutral → start at cyan

        double hue;
        if (max == rd)
            hue = 60 * (((gd - bd) / delta) % 6);
        else if (max == gd)
            hue = 60 * ((bd - rd) / delta + 2);
        else
            hue = 60 * ((rd - gd) / delta + 4);

        if (hue < 0) hue += 360;
        return hue;
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
