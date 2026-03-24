namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Slow sinusoidal brightness oscillation — a gentle breathing pattern.
/// </summary>
public class BreathingRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Breathing;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 4000);
        var (r, g, b) = GetPrimaryColor(keyframe);
        var rateHz = GetParam(keyframe, "pulse_rate_hz", 0.25);

        // Already slow — no simplification needed (same output either way)
        var stepMs = 200;
        var steps = (int)(duration / stepMs);
        var commands = new List<EffectCommand>(steps);

        for (int i = 0; i < steps; i++)
        {
            var t = i * stepMs;
            // Sine wave: 0→1→0 per cycle, starting from 0
            var sineVal = (Math.Sin(2 * Math.PI * rateHz * t / 1000.0 - Math.PI / 2) + 1) / 2;
            // Apply an ease curve for more natural breathing (power of 1.5)
            var eased = Math.Pow(sineVal, 1.5);
            var brightness = ScaleBrightness((uint)(eased * 100), intensity, cap);

            commands.Add(new EffectCommand((uint)t, r, g, b, brightness, (uint)stepMs));
        }

        return commands;
    }
}
