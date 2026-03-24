namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Rapid orange/red burst with slow decay to dim ember.
/// </summary>
public class ExplosionRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Explosion;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 1500);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        if (ShouldSimplify(context))
        {
            var halfDuration = duration / 2;
            var midBrightness = ScaleBrightness(60, intensity, cap);
            return new List<EffectCommand>
            {
                new EffectCommand(0, 255, 140, 20, midBrightness, (uint)halfDuration),
                new EffectCommand((uint)halfDuration, 255, 140, 20, 0, (uint)halfDuration),
            };
        }

        // Instant bright orange burst
        var phase1 = duration / 5;       // orange peak
        var phase2 = duration * 2 / 5;   // shift to red
        var phase3 = duration * 2 / 5;   // dim ember fade

        var emberBrightness = ScaleBrightness(15, intensity, cap);
        var redBrightness = ScaleBrightness(50, intensity, cap);

        return new List<EffectCommand>
        {
            // Instant orange flash
            new EffectCommand(0, 255, 160, 30, peakBrightness, 0),
            // Decay to red
            new EffectCommand((uint)phase1, 220, 60, 10, redBrightness, (uint)phase2),
            // Fade to dim ember
            new EffectCommand((uint)(phase1 + phase2), 150, 30, 5, emberBrightness, (uint)phase3),
            // Out
            new EffectCommand((uint)duration, 100, 20, 0, 0, 200),
        };
    }
}
