namespace OpenLightFX.Emby.Effects.Renderers;

using Openlightfx;

/// <summary>
/// White flashes a rate-limited bulb can still show: 120 ms peaks, a 30% dip
/// between flashes, then four 120 ms steps from amber toward the scene colour.
/// The window does not end at brightness 0 (WiZ turns the bulb off for that).
/// Mirror of openlightfx-studio src/lib/domain/effects.ts lightning().
/// </summary>
public class LightningRenderer : BaseEffectRenderer
{
    private const int PeakMs = 120;
    private const int Steps = 4;

    public override EffectType EffectType => EffectType.Lightning;

    public override List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context)
    {
        if (!BulbMeetsCapability(keyframe, context))
            return GetFallback(keyframe, context);

        var intensity = keyframe.Intensity > 0 ? keyframe.Intensity : 100;
        var cap = context.GlobalBrightnessCap;
        var flashCount = (int)GetParam(keyframe, "flash_count", 1);
        var peak = ScaleBrightness(100, intensity, cap);
        var floor = peak == 0 ? 0u : 1u;
        var dip = peak == 0 ? 0u : Math.Max(floor, ScaleBrightness(30, intensity, cap));
        var afterglow = ScaleBrightness(40, intensity, cap);
        var (sceneR, sceneG, sceneB, sceneBrightness) = RestTarget(context, intensity, cap, peak);

        if (ShouldSimplify(context))
        {
            var simple = new List<EffectCommand>
            {
                new EffectCommand(0, 255, 255, 255, peak, 0),
                new EffectCommand(PeakMs, 255, 255, 255, dip, 0),
            };
            simple.AddRange(Tail(PeakMs * 2, afterglow, sceneR, sceneG, sceneB, sceneBrightness, floor));
            return simple;
        }

        var commands = new List<EffectCommand>();
        uint offset = 0;
        flashCount = Math.Clamp(flashCount, 1, 4);

        for (int i = 0; i < flashCount; i++)
        {
            commands.Add(new EffectCommand(offset, 255, 255, 255, peak, 0));
            offset += PeakMs;
            if (i < flashCount - 1)
            {
                commands.Add(new EffectCommand(offset, 255, 255, 255, dip, 0));
                offset += PeakMs;
            }
        }

        commands.AddRange(Tail(offset, afterglow, sceneR, sceneG, sceneB, sceneBrightness, floor));
        return commands;
    }

    private (byte R, byte G, byte B, uint Brightness) RestTarget(
        EffectContext context, uint intensity, uint cap, uint peak)
    {
        if (context.Scene is { } scene)
        {
            var brightness = peak == 0 ? 0u : Math.Max(1u, Math.Min(100u, scene.Brightness));
            return (scene.R, scene.G, scene.B, brightness);
        }

        var rest = peak == 0 ? 0u : Math.Max(1, ScaleBrightness(15, intensity, cap));
        return (255, 180, 80, rest);
    }

    private static List<EffectCommand> Tail(
        uint start, uint afterglow, byte sceneR, byte sceneG, byte sceneB, uint sceneBrightness, uint floor)
    {
        const byte glowR = 255;
        const byte glowG = 180;
        const byte glowB = 80;
        var span = Steps - 1;
        var commands = new List<EffectCommand>(Steps);
        var offset = start;
        for (var step = 0; step < Steps; step++)
        {
            commands.Add(new EffectCommand(
                offset,
                Mix(glowR, sceneR, step, span),
                Mix(glowG, sceneG, step, span),
                Mix(glowB, sceneB, step, span),
                MixBrightness(afterglow, sceneBrightness, step, span, floor),
                PeakMs));
            offset += PeakMs;
        }

        return commands;
    }

    private static byte Mix(byte from, byte to, int step, int span) =>
        (byte)((from * (span - step) + to * step) / span);

    private static uint MixBrightness(uint from, uint to, int step, int span, uint floor) =>
        Math.Max(floor, (from * (uint)(span - step) + to * (uint)step) / (uint)span);
}
