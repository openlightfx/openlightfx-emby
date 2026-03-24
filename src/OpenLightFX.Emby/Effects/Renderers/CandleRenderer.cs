namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Subtle warm flicker simulating a candle (~2700K equivalent), gentler than flame.
/// </summary>
public class CandleRenderer : BaseEffectRenderer
{
    // Warm white approximating 2700K
    private const byte WarmR = 255;
    private const byte WarmG = 170;
    private const byte WarmB = 80;

    public override EffectType EffectType => EffectType.Candle;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 3000);

        if (ShouldSimplify(context))
        {
            var bright = ScaleBrightness(65, intensity, cap);
            var dim = ScaleBrightness(50, intensity, cap);
            var halfDuration = duration / 2;
            return new List<EffectCommand>
            {
                new EffectCommand(0, WarmR, WarmG, WarmB, bright, (uint)halfDuration),
                new EffectCommand((uint)halfDuration, WarmR, WarmG, WarmB, dim, (uint)halfDuration),
            };
        }

        var stepMs = 100;
        var steps = (int)(duration / stepMs);
        var commands = new List<EffectCommand>(steps);

        for (int i = 0; i < steps; i++)
        {
            var t = i * stepMs;
            // Subtle low-frequency variation (1-3 Hz range)
            var wave1 = Math.Sin(2 * Math.PI * 1.1 * t / 1000.0) * 0.05;
            var wave2 = Math.Sin(2 * Math.PI * 2.3 * t / 1000.0) * 0.03;
            var wave3 = Math.Sin(2 * Math.PI * 0.4 * t / 1000.0) * 0.02;
            var variation = wave1 + wave2 + wave3; // roughly -0.1 to +0.1

            var baseLuminance = 0.6 + variation;
            baseLuminance = Math.Clamp(baseLuminance, 0.45, 0.75);

            var brightness = ScaleBrightness((uint)(baseLuminance * 100), intensity, cap);
            commands.Add(new EffectCommand((uint)t, WarmR, WarmG, WarmB, brightness, (uint)stepMs));
        }

        return commands;
    }
}
