namespace OpenLightFX.Emby.Effects;

/// <summary>
/// Post-processing filter that softens effect output for photosensitivity safety.
/// Applied to the command list produced by effect renderers when photosensitivity mode is enabled.
/// </summary>
public static class PhotosensitivityFilter
{
    private const uint MaxFlashFrequencyHz = 3;
    private const uint MinTimeBetweenFlashesMs = 1000 / MaxFlashFrequencyHz; // ~333ms
    private const uint MaxBrightnessDelta = 50; // max 50% brightness change per transition
    private const uint MinTransitionMs = 200; // minimum transition time for instant changes

    /// <summary>
    /// Apply photosensitivity filtering to a list of effect commands.
    /// Returns a new filtered list (does not modify input).
    /// </summary>
    public static List<EffectCommand> Apply(List<EffectCommand> commands)
    {
        if (commands.Count == 0) return commands;

        var filtered = new List<EffectCommand>(commands.Count);

        uint lastBrightness = commands[0].Brightness;
        uint lastOffsetMs = 0;
        int flashCount = 0;
        uint windowStartMs = 0;

        foreach (var cmd in commands)
        {
            var newCmd = cmd;

            // Enforce minimum transition time
            if (newCmd.TransitionMs < MinTransitionMs)
                newCmd = newCmd with { TransitionMs = MinTransitionMs };

            // Clamp brightness delta
            var delta = Math.Abs((int)newCmd.Brightness - (int)lastBrightness);
            if (delta > MaxBrightnessDelta)
            {
                uint clampedBrightness = newCmd.Brightness > lastBrightness
                    ? Math.Min(lastBrightness + MaxBrightnessDelta, 100)
                    : (uint)Math.Max((int)lastBrightness - (int)MaxBrightnessDelta, 0);
                newCmd = newCmd with { Brightness = clampedBrightness };
            }

            // Flash frequency limiting: a "flash" is a significant brightness change (>20%)
            bool isFlash = Math.Abs((int)newCmd.Brightness - (int)lastBrightness) > 20;

            if (isFlash)
            {
                // Reset window if we've moved past it
                if (newCmd.OffsetMs - windowStartMs > 1000)
                {
                    windowStartMs = newCmd.OffsetMs;
                    flashCount = 0;
                }

                flashCount++;

                // If we've exceeded max flashes in this 1-second window, suppress
                if (flashCount > MaxFlashFrequencyHz)
                {
                    newCmd = newCmd with { Brightness = lastBrightness };
                }

                // Enforce minimum time between flashes
                if (newCmd.OffsetMs - lastOffsetMs < MinTimeBetweenFlashesMs && filtered.Count > 0)
                {
                    newCmd = newCmd with { Brightness = lastBrightness };
                }
            }

            filtered.Add(newCmd);
            lastBrightness = newCmd.Brightness;
            lastOffsetMs = newCmd.OffsetMs;
        }

        return filtered;
    }
}
