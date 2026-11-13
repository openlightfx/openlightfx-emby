namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Cookie names and header formatting for Studio's cookie tickets. A cookie never carries
/// the Emby access token itself; it carries an opaque ticket from <see cref="StudioCookieTickets"/>.
/// </summary>
public static class StudioAssetAuth
{
    /// <summary>Older builds stored the raw Emby access token here. It is cleared, never read.</summary>
    public const string LegacyCookieName = "olfx_studio_auth";

    public static string? ReadCookie(string? cookieHeader, string name)
    {
        if (string.IsNullOrEmpty(cookieHeader))
            return null;

        foreach (var part in cookieHeader.Split(';'))
        {
            var trimmed = part.Trim();
            var split = trimmed.IndexOf('=');
            if (split <= 0)
                continue;
            if (!trimmed[..split].Trim().Equals(name, StringComparison.Ordinal))
                continue;

            var raw = trimmed[(split + 1)..].Trim();
            return Uri.UnescapeDataString(raw);
        }

        return null;
    }

    /// <summary>HttpOnly, SameSite=Strict, Secure on HTTPS, and a bounded lifetime.</summary>
    public static string SetCookieHeader(string name, string value, bool secure, TimeSpan maxAge)
    {
        var seconds = Math.Max(0, (long)maxAge.TotalSeconds);
        var secureFlag = secure ? "; Secure" : "";
        return $"{name}={Uri.EscapeDataString(value)}; Path=/; Max-Age={seconds}; HttpOnly; SameSite=Strict{secureFlag}";
    }

    public static string ClearCookieHeader(string name, bool secure)
    {
        var secureFlag = secure ? "; Secure" : "";
        return $"{name}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict{secureFlag}";
    }
}
