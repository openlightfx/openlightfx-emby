namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Intense white flash followed by slow linear fade.
/// </summary>
public class FlashbangRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Flashbang;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 2000);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        if (ShouldSimplify(context))
        {
            var fadeMs = Math.Max(duration - 200, 200);
            return new List<EffectCommand>
            {
                new EffectCommand(0, 255, 255, 255, 0, 0),
                new EffectCommand(0, 255, 255, 255, peakBrightness, 200),
                new EffectCommand(200, 255, 255, 255, 0, (uint)fadeMs),
            };
        }

        var fadeDuration = Math.Max(duration - 100, 100);
        return new List<EffectCommand>
        {
            new EffectCommand(0, 255, 255, 255, peakBrightness, 0),
            new EffectCommand(100, 255, 255, 255, 0, (uint)fadeDuration),
        };
    }
}
