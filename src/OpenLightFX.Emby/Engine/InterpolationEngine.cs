using Openlightfx;

namespace OpenLightFX.Emby.Engine;

/// <summary>
/// Handles interpolation between keyframes during playback.
/// </summary>
public static class InterpolationEngine
{
    public record struct InterpolatedState(
        byte R, byte G, byte B,
        uint Brightness,
        uint ColorTemperature,
        ColorMode ColorMode,
        bool PowerOn);

    /// <summary>
    /// Applies an easing function to a linear progress value.
    /// </summary>
    /// <param name="mode">The interpolation mode defining the easing curve.</param>
    /// <param name="t">Linear progress from 0 to 1.</param>
    /// <returns>Eased progress value from 0 to 1.</returns>
    public static double ApplyEasing(InterpolationMode mode, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);

        return mode switch
        {
            InterpolationMode.Step => t < 1.0 ? 0.0 : 1.0,
            InterpolationMode.Linear => t,
            _ => t, // Unspecified defaults to linear
        };
    }

    /// <summary>
    /// Interpolates between two keyframes at a given playback time.
    /// The transition ends at <c>next.TimestampMs</c> and begins at
    /// <c>next.TimestampMs - next.TransitionMs</c>.
    /// </summary>
    public static InterpolatedState Interpolate(
        Keyframe? previous, Keyframe next, ulong currentTimeMs)
    {
        var nextState = KeyframeToState(next);

        ulong transitionDuration = next.TransitionMs;
        ulong transitionEnd = next.TimestampMs;
        ulong transitionStart = transitionDuration <= transitionEnd
            ? transitionEnd - transitionDuration
            : 0;

        // Past (or at) the target timestamp — snap to next regardless of transition
        if (currentTimeMs >= transitionEnd)
            return nextState;

        // Zero-length transition: hold previous state until we actually reach next.
        // "next" is the upcoming keyframe — we must not snap to it before its timestamp.
        if (transitionDuration == 0)
            return previous != null ? KeyframeToState(previous) : DefaultState();

        // Before the transition window — hold previous state
        if (currentTimeMs <= transitionStart)
            return previous != null ? KeyframeToState(previous) : DefaultState();

        // Inside the transition window — blend
        var prevState = previous != null ? KeyframeToState(previous) : DefaultState();
        double t = (double)(currentTimeMs - transitionStart) / (double)(transitionEnd - transitionStart);
        double eased = ApplyEasing(next.Interpolation, t);

        return BlendStates(prevState, nextState, eased);
    }

    /// <summary>
    /// Finds the bracketing keyframes for the given time and returns the
    /// interpolated state. Uses binary search for efficiency.
    /// </summary>
    public static InterpolatedState GetStateAtTime(
        IReadOnlyList<Keyframe> channelKeyframes, ulong timeMs,
        byte defaultR, byte defaultG, byte defaultB, uint defaultBrightness)
    {
        if (channelKeyframes.Count == 0)
            return new InterpolatedState(defaultR, defaultG, defaultB, defaultBrightness, 0, ColorMode.Rgb, false);

        // Binary search: find the first keyframe with TimestampMs > timeMs
        int lo = 0, hi = channelKeyframes.Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (channelKeyframes[mid].TimestampMs <= timeMs)
                lo = mid + 1;
            else
                hi = mid;
        }

        int nextIdx = lo;

        // Past all keyframes — hold last keyframe state
        if (nextIdx >= channelKeyframes.Count)
            return KeyframeToState(channelKeyframes[^1]);

        Keyframe next = channelKeyframes[nextIdx];

        // Before the first keyframe's transition window — return defaults
        if (nextIdx == 0)
        {
            ulong transStart = next.TransitionMs <= next.TimestampMs
                ? next.TimestampMs - next.TransitionMs
                : 0;
            if (timeMs < transStart)
                return new InterpolatedState(defaultR, defaultG, defaultB, defaultBrightness, 0, ColorMode.Rgb, false);
        }

        Keyframe? prev = nextIdx > 0 ? channelKeyframes[nextIdx - 1] : null;
        return Interpolate(prev, next, timeMs);
    }

    // ── Private helpers ──────────────────────────────────────────────

    private static InterpolatedState DefaultState() =>
        new(0, 0, 0, 0, 0, ColorMode.Rgb, false);

    private static InterpolatedState KeyframeToState(Keyframe kf)
    {
        byte r = 0, g = 0, b = 0;

        if (kf.ColorMode == ColorMode.ColorTemperature)
        {
            (r, g, b) = ColorTempToRgb(kf.ColorTemperature);
        }
        else if (kf.Color != null)
        {
            r = (byte)Math.Min(kf.Color.R, 255);
            g = (byte)Math.Min(kf.Color.G, 255);
            b = (byte)Math.Min(kf.Color.B, 255);
        }

        // Only forward ColorTemperature when the mode is actually ColorTemperature.
        // RGB keyframes may have a non-zero colorTemp field (e.g. ambient temperature
        // metadata from Studio) but it must not override the RGB dispatch path.
        var colorTemp = kf.ColorMode == ColorMode.ColorTemperature ? kf.ColorTemperature : 0u;

        return new InterpolatedState(
            r, g, b,
            kf.Brightness,
            colorTemp,
            kf.ColorMode,
            kf.PowerOn);
    }

    private static InterpolatedState BlendStates(
        InterpolatedState from, InterpolatedState to, double t)
    {
        bool bothColorTemp = from.ColorMode == ColorMode.ColorTemperature
                          && to.ColorMode == ColorMode.ColorTemperature;

        if (bothColorTemp)
        {
            // Interpolate in kelvin space and derive RGB from result
            uint kelvin = LerpUint(from.ColorTemperature, to.ColorTemperature, t);
            var (r, g, b) = ColorTempToRgb(kelvin);
            return new InterpolatedState(
                r, g, b,
                LerpUint(from.Brightness, to.Brightness, t),
                kelvin,
                ColorMode.ColorTemperature,
                from.PowerOn || to.PowerOn);
        }

        // For RGB or cross-model blending, KeyframeToState already converted
        // color temperature to RGB so we can interpolate directly.
        return new InterpolatedState(
            LerpByte(from.R, to.R, t),
            LerpByte(from.G, to.G, t),
            LerpByte(from.B, to.B, t),
            LerpUint(from.Brightness, to.Brightness, t),
            0,
            ColorMode.Rgb,
            from.PowerOn || to.PowerOn);
    }

    private static byte LerpByte(byte a, byte b, double t) =>
        (byte)Math.Clamp(Math.Round(a + (b - (double)a) * t), 0, 255);

    private static uint LerpUint(uint a, uint b, double t) =>
        (uint)Math.Max(Math.Round(a + (b - (double)a) * t), 0);

    /// <summary>
    /// Approximates RGB values for a color temperature in kelvin
    /// using the Tanner Helland algorithm.
    /// </summary>
    private static (byte R, byte G, byte B) ColorTempToRgb(uint kelvin)
    {
        double temp = Math.Clamp(kelvin, 1000, 40000) / 100.0;
        double r, g, b;

        if (temp <= 66)
        {
            r = 255;
            g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
            b = temp <= 19 ? 0 : 138.5177312231 * Math.Log(temp - 10) - 305.0447927307;
        }
        else
        {
            r = 329.698727446 * Math.Pow(temp - 60, -0.1332047592);
            g = 288.1221695283 * Math.Pow(temp - 60, -0.0755148492);
            b = 255;
        }

        return (
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255));
    }
}
