namespace OpenLightFX.Emby.Utilities;

using Openlightfx;

/// <summary>
/// Helper for generating safety warnings based on track SafetyInfo.
/// </summary>
public static class SafetyWarningHelper
{
    public static string? GetWarningMessage(SafetyInfo? safetyInfo, bool photosensitivityEnabled)
    {
        if (safetyInfo == null) return null;

        var warnings = new List<string>();

        if (safetyInfo.ContainsFlashing)
            warnings.Add("This track contains rapid flashing effects");

        if (safetyInfo.ContainsStrobing)
            warnings.Add("This track contains strobing effects");

        if (warnings.Count == 0) return null;

        var message = string.Join(". ", warnings) + ".";

        if (!string.IsNullOrEmpty(safetyInfo.WarningText))
            message += $" Author note: {safetyInfo.WarningText}";

        if (photosensitivityEnabled)
            message += " Photosensitivity mode is ON — effects have been automatically softened.";

        if (safetyInfo.IntensityRating >= IntensityRating.Intense)
            message += $" Intensity rating: {safetyInfo.IntensityRating}.";

        return message;
    }
}
