namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// Brief sharp white/yellow flashes simulating gunfire muzzle flash.
/// </summary>
public class GunfireRenderer : BaseEffectRenderer
{
    public override EffectType EffectType => EffectType.Gunfire;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var duration = (int)(keyframe.DurationMs > 0 ? keyframe.DurationMs : 500);
        var peakBrightness = ScaleBrightness(100, intensity, cap);

        var defaultCount = Math.Max(duration / 100, 1);
        var flashCount = (int)GetParam(keyframe, "flash_count", defaultCount);
        flashCount = Math.Max(flashCount, 1);

        if (ShouldSimplify(context))
        {
            return new List<EffectCommand>
            {
                new EffectCommand(0, 255, 240, 180, peakBrightness, 0),
                new EffectCommand(50, 255, 240, 180, 0, 100),
            };
        }

        var commands = new List<EffectCommand>();
        var cycleMs = Math.Max((int)(duration / flashCount), 50);

        for (int i = 0; i < flashCount; i++)
        {
            uint offset = (uint)(i * cycleMs);
            // 30ms yellow-white flash, 70ms dark gap (or proportional)
            commands.Add(new EffectCommand(offset, 255, 240, 180, peakBrightness, 0));
            commands.Add(new EffectCommand(offset + 30, 255, 240, 180, 0, 0));
        }

        return commands;
    }
}
