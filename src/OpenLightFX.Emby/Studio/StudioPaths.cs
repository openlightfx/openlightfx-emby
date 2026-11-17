namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Public paths for the embedded Studio editor.
/// Emby mounts plugin routes at the server root and again under /emby.
/// </summary>
public static class StudioPaths
{
    /// <summary>ServiceStack route prefix, relative to the Emby server root.</summary>
    public const string RoutePrefix = "/OpenLightFX/studio";

    /// <summary>URL path opened from the dashboard. Emby's default base path is /emby.</summary>
    public const string PublicUrl = "/emby/OpenLightFX/studio/";

    /// <summary>Studio bulbs screen. Relative, so it stays on the server that served the page.</summary>
    public const string BulbsUrl = PublicUrl + "#/bulbs";
}
