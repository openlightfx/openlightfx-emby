namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Canonicalizes the Studio root onto a trailing slash so relative asset URLs stay
/// under <c>/studio/</c>. The Location is path-only, which keeps working behind a
/// reverse proxy that terminates HTTPS.
/// </summary>
public static class StudioRootRedirect
{
    private const string StudioRootSuffix = "/OpenLightFX/studio";

    /// <summary>
    /// Returns a path-and-query Location when one of the request URLs is the Studio
    /// root without a trailing slash. A URL that already has the slash suppresses
    /// the redirect. Emby removes the trailing slash from <c>PathInfo</c> before
    /// the service runs, so pass <c>RawUrl</c> and <c>AbsoluteUri</c> as well.
    /// </summary>
    public static string? Target(params string?[] requestUrls)
    {
        var matches = new List<StudioRootMatch>();
        foreach (var candidate in requestUrls)
        {
            if (TryMatch(candidate, out var match))
                matches.Add(match);
        }

        if (matches.Count == 0 || matches.Exists(match => match.HasTrailingSlash))
            return null;

        var best = matches
            .OrderByDescending(match => match.Path.Length)
            .ThenByDescending(match => match.Query.Length)
            .First();
        var query = best.Query.Length > 0
            ? best.Query
            : matches.Select(match => match.Query).FirstOrDefault(query => query.Length > 0) ?? "";

        return best.Path + "/" + query;
    }

    private static bool TryMatch(string? value, out StudioRootMatch match)
    {
        match = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var pathAndQuery = ExtractPathAndQuery(value);
        if (pathAndQuery == null || !pathAndQuery.StartsWith('/'))
            return false;

        var split = pathAndQuery.IndexOf('?');
        var path = split >= 0 ? pathAndQuery[..split] : pathAndQuery;
        var query = split >= 0 ? pathAndQuery[split..] : "";
        if (path.Length == 0)
            return false;

        var hasTrailingSlash = path.EndsWith('/');
        var withoutSlash = hasTrailingSlash ? path.TrimEnd('/') : path;
        if (!withoutSlash.EndsWith(StudioRootSuffix, StringComparison.OrdinalIgnoreCase))
            return false;

        // Keep the caller's casing and any /emby prefix. Only the root itself matches.
        match = new StudioRootMatch(hasTrailingSlash ? withoutSlash : path, query, hasTrailingSlash);
        return true;
    }

    private static string? ExtractPathAndQuery(string value)
    {
        if (value.StartsWith('/'))
            return value;

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
            return null;

        var pathStart = value.IndexOf('/', scheme + 3);
        return pathStart < 0 ? "/" : value[pathStart..];
    }

    private readonly record struct StudioRootMatch(string Path, string Query, bool HasTrailingSlash);
}
