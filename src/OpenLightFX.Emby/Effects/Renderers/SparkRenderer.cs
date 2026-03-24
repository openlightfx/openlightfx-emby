namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Very brief single flash — a sharp spark of light.
/// </summary>
public class SparkRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Spark;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var (r, g, b) = GetPrimaryColor(keyframe);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        if (ShouldSimplify(context))
        {
            return new List<EffectCommand>
            {
                new EffectCommand(0, r, g, b, peakBrightness, 0),
                new EffectCommand(100, r, g, b, 0, 0),
            };
        }

        return new List<EffectCommand>
        {
            new EffectCommand(0, r, g, b, peakBrightness, 0),
            new EffectCommand(30, r, g, b, 0, 0),
        };
    }
}
