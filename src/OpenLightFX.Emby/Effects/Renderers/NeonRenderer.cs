namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Color-saturated neon sign with brief deterministic off-flickers.
/// </summary>
public class NeonRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Neon;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 2000);
        var (r, g, b) = GetPrimaryColor(keyframe);
        var steadyBrightness = ScaleBrightness(90, intensity, cap);
        var dimBrightness = ScaleBrightness(20, intensity, cap);

        if (ShouldSimplify(context))
        {
            // Steady color with one gentle dim in the middle
            var third = duration / 3;
            return new List<EffectCommand>
            {
                new EffectCommand(0, r, g, b, steadyBrightness, 200),
                new EffectCommand((uint)third, r, g, b, dimBrightness, 200),
                new EffectCommand((uint)(third + 200), r, g, b, steadyBrightness, 200),
            };
        }

        var commands = new List<EffectCommand>();
        // Start with steady on
        commands.Add(new EffectCommand(0, r, g, b, steadyBrightness, 0));

        // Deterministic flicker pattern using a seeded approach based on duration
        // Insert brief 20ms off-flickers at pseudo-random but deterministic intervals
        uint seed = (uint)(duration * 7 + 13);
        uint offset = 100;

        while (offset < duration - 40)
        {
            // Simple deterministic sequence: use seed to decide gap
            seed = (seed * 1103515245 + 12345) & 0x7FFFFFFF;
            var gap = 150 + (int)(seed % 400); // 150-550ms between flickers

            offset += (uint)gap;
            if (offset >= duration - 40) break;

            // Brief flicker off and back on
            commands.Add(new EffectCommand(offset, r, g, b, dimBrightness, 0));
            commands.Add(new EffectCommand(offset + 20, r, g, b, steadyBrightness, 0));
        }

        return commands;
    }
}
