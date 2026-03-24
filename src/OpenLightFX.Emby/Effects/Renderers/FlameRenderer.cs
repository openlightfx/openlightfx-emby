namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Flickering warm colors simulating fire using deterministic sine-based patterns.
/// </summary>
public class FlameRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Flame;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 1000);
        var flickerHz = GetParam(keyframe, "flicker_rate_hz", 8);

        if (ShouldSimplify(context))
        {
            var bright = ScaleBrightness(80, intensity, cap);
            var dim = ScaleBrightness(40, intensity, cap);
            var halfDuration = duration / 2;

            return new List<EffectCommand>
            {
                new EffectCommand(0, 255, 120, 20, bright, (uint)halfDuration),
                new EffectCommand((uint)halfDuration, 200, 80, 10, dim, (uint)halfDuration),
            };
        }

        var commands = new List<EffectCommand>();
        var stepMs = Math.Max(1000.0 / flickerHz / 2, 20);
        var steps = (int)(duration / stepMs);

        for (int i = 0; i < steps; i++)
        {
            var t = i * stepMs;
            // Layered sine waves for organic flicker
            var wave1 = Math.Sin(2 * Math.PI * flickerHz * t / 1000.0);
            var wave2 = Math.Sin(2 * Math.PI * flickerHz * 1.7 * t / 1000.0) * 0.3;
            var wave3 = Math.Sin(2 * Math.PI * flickerHz * 0.3 * t / 1000.0) * 0.2;
            var combined = (wave1 + wave2 + wave3 + 1.5) / 3.0; // normalize ~0..1

            var brightness = ScaleBrightness((uint)(40 + combined * 60), intensity, cap);

            // Shift color: brighter = more yellow, dimmer = more red
            var r = (byte)(200 + (int)(combined * 55));
            var g = (byte)(60 + (int)(combined * 100));
            var b = (byte)(5 + (int)(combined * 20));

            commands.Add(new EffectCommand((uint)t, r, g, b, brightness, (uint)stepMs));
        }

        return commands;
    }
}
