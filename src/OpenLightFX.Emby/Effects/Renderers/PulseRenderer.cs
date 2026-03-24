namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Rhythmic sinusoidal brightness oscillation on primary color.
/// </summary>
public class PulseRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Pulse;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 2000);
        var (r, g, b) = GetPrimaryColor(keyframe);
        var rateHz = GetParam(keyframe, "pulse_rate_hz", 1);

        if (ShouldSimplify(context))
            rateHz = Math.Min(rateHz, 2);

        // Generate sine wave samples; step interval based on rate
        var stepMs = Math.Max(1000.0 / rateHz / 8, 30);
        var steps = (int)(duration / stepMs);
        var commands = new List<EffectCommand>(steps);

        for (int i = 0; i < steps; i++)
        {
            var t = i * stepMs;
            var sineVal = (Math.Sin(2 * Math.PI * rateHz * t / 1000.0 - Math.PI / 2) + 1) / 2;
            var brightness = ScaleBrightness((uint)(sineVal * 100), intensity, cap);
            commands.Add(new EffectCommand((uint)t, r, g, b, brightness, (uint)stepMs));
        }

        return commands;
    }
}
