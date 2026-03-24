namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Rapid on/off flashing at configurable frequency.
/// </summary>
public class StrobeRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Strobe;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 1000);
        var (r, g, b) = GetPrimaryColor(keyframe);
        var rateHz = GetParam(keyframe, "flicker_rate_hz", 10);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        if (ShouldSimplify(context))
        {
            // Convert strobe to slow pulse
            var halfDuration = duration / 2;
            var midBrightness = ScaleBrightness(50, intensity, cap);
            return new List<EffectCommand>
            {
                new EffectCommand(0, r, g, b, peakBrightness, (uint)halfDuration),
                new EffectCommand((uint)halfDuration, r, g, b, midBrightness, (uint)halfDuration),
            };
        }

        var halfCycleMs = Math.Max(1000.0 / rateHz / 2, 10);
        var commands = new List<EffectCommand>();
        uint offset = 0;
        bool on = true;

        while (offset < duration)
        {
            commands.Add(new EffectCommand(offset, r, g, b, on ? peakBrightness : 0, 0));
            offset += (uint)halfCycleMs;
            on = !on;
        }

        return commands;
    }
}
