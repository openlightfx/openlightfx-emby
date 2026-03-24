namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Rapid bright white flash(es) with afterglow decay.
/// </summary>
public class LightningRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Lightning;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var flashCount = (int)GetParam(keyframe, "flash_count", 1);
        var decayMs = (uint)GetParam(keyframe, "decay_ms", 500);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        if (ShouldSimplify(context))
        {
            return new List<EffectCommand>
            {
                new EffectCommand(0, 255, 255, 255, peakBrightness, 0),
                new EffectCommand(50, 255, 255, 255, 0, 500),
            };
        }

        var commands = new List<EffectCommand>();
        uint offset = 0;

        flashCount = Math.Clamp(flashCount, 1, 4);

        for (int i = 0; i < flashCount; i++)
        {
            commands.Add(new EffectCommand(offset, 255, 255, 255, peakBrightness, 0));
            offset += 50;
            commands.Add(new EffectCommand(offset, 255, 255, 255, 0, 0));
            offset += 50;
        }

        // Amber afterglow decay
        var afterglowBrightness = ScaleBrightness(40, intensity, cap);
        commands.Add(new EffectCommand(offset, 255, 180, 80, afterglowBrightness, 50));
        commands.Add(new EffectCommand(offset + 50, 255, 180, 80, 0, decayMs));

        return commands;
    }
}
