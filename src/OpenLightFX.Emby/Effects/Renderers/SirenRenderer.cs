namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Alternating primary/secondary color flashing (default red/blue).
/// </summary>
public class SirenRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Siren;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 2000);
        var (pr, pg, pb) = GetPrimaryColor(keyframe);
        var (sr, sg, sb) = GetSecondaryColor(keyframe);

        // Default to red/blue when both are at default (white/black)
        if (keyframe.PrimaryColor == null) { pr = 255; pg = 0; pb = 0; }
        if (keyframe.SecondaryColor == null) { sr = 0; sg = 0; sb = 255; }

        var rateHz = GetParam(keyframe, "pulse_rate_hz", 2);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        if (ShouldSimplify(context))
            rateHz = Math.Min(rateHz, 1);

        var halfCycleMs = Math.Max(1000.0 / rateHz / 2, 50);
        var transitionMs = (uint)Math.Min(halfCycleMs / 4, 50);
        var commands = new List<EffectCommand>();
        uint offset = 0;
        bool usePrimary = true;

        while (offset < duration)
        {
            if (usePrimary)
                commands.Add(new EffectCommand(offset, pr, pg, pb, peakBrightness, transitionMs));
            else
                commands.Add(new EffectCommand(offset, sr, sg, sb, peakBrightness, transitionMs));

            offset += (uint)halfCycleMs;
            usePrimary = !usePrimary;
        }

        return commands;
    }
}
