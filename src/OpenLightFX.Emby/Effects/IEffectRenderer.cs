namespace OpenLightFX.Emby.Effects;

using Openlightfx;

/// <summary>
/// Interface for effect renderers that expand EffectKeyframes into concrete bulb commands.
/// </summary>
public interface IEffectRenderer
{
    EffectType EffectType { get; }

    /// <summary>
    /// Render the effect into a sequence of timed bulb commands.
    /// Returns commands with OffsetMs relative to the effect start time.
    /// The renderer should adapt based on bulb capabilities in the context.
    /// </summary>
    List<EffectCommand> Render(EffectKeyframe keyframe, EffectContext context);
}
