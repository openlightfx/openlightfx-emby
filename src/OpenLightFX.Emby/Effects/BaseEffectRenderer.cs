namespace OpenLightFX.Emby.Effects;

using Openlightfx;

/// <summary>
/// Base class providing common effect rendering utilities.
/// </summary>
public abstract class BaseEffectRenderer : IEffectRenderer
{
    public abstract EffectType EffectType { get; }

    public abstract List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context);

    /// <summary>
    /// Scale brightness by intensity (0-100) and global brightness cap.
    /// </summary>
    protected uint ScaleBrightness(uint baseBrightness, uint intensity, uint brightnessCap)
    {
        var scaled = baseBrightness * intensity / 100;
        return Math.Min(scaled * brightnessCap / 100, 100);
    }

    /// <summary>
    /// Get the effective primary color, defaulting to white if not specified.
    /// </summary>
    protected (byte R, byte G, byte B) GetPrimaryColor(EffectKeyframe keyframe)
    {
        if (keyframe.PrimaryColor != null)
            return ((byte)keyframe.PrimaryColor.R, (byte)keyframe.PrimaryColor.G, (byte)keyframe.PrimaryColor.B);
        return (255, 255, 255);
    }

    /// <summary>
    /// Get the effective secondary color, defaulting to black if not specified.
    /// </summary>
    protected (byte R, byte G, byte B) GetSecondaryColor(EffectKeyframe keyframe)
    {
        if (keyframe.SecondaryColor != null)
            return ((byte)keyframe.SecondaryColor.R, (byte)keyframe.SecondaryColor.G, (byte)keyframe.SecondaryColor.B);
        return (0, 0, 0);
    }

    /// <summary>
    /// Get a named double parameter from the effect params, or a default value.
    /// </summary>
    protected double GetParam(EffectKeyframe keyframe, string name, double defaultValue)
    {
        if (keyframe.EffectParams?.Params != null && keyframe.EffectParams.Params.TryGetValue(name, out var val))
            return val;
        return defaultValue;
    }

    /// <summary>
    /// Get the fallback command when the bulb can't render this effect.
    /// </summary>
    protected List<EffectCommand> GetFallback(EffectKeyframe keyframe, EffectContext context)
    {
        if (keyframe.FallbackColor != null)
        {
            var brightness = ScaleBrightness(keyframe.FallbackBrightness > 0 ? keyframe.FallbackBrightness : 50,
                100, context.GlobalBrightnessCap);
            return new List<EffectCommand>
            {
                new EffectCommand(0,
                    (byte)keyframe.FallbackColor.R,
                    (byte)keyframe.FallbackColor.G,
                    (byte)keyframe.FallbackColor.B,
                    brightness, 200)
            };
        }
        return new List<EffectCommand>();
    }

    /// <summary>
    /// Check if the bulb meets the required capability for this effect keyframe.
    /// </summary>
    protected bool BulbMeetsCapability(EffectKeyframe keyframe, EffectContext context)
    {
        return context.BulbCapabilities.MeetsCapability(keyframe.RequiredCapability);
    }

    /// <summary>
    /// Determine if simplified rendering should be used (bulb is slow but has RGB).
    /// </summary>
    protected bool ShouldSimplify(EffectContext context)
    {
        return !context.BulbCapabilities.HasFastTransition;
    }
}
