namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Browser loads of Studio cannot send X-Emby-Token on script and stylesheet requests.
/// The first authenticated hit sets an HttpOnly cookie that those follow-up requests carry.
/// </summary>
public static class StudioAssetAuth
{
    public const string CookieName = "olfx_studio_auth";

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

    public static string SetCookieHeader(string token, bool secure)
    {
        var value = Uri.EscapeDataString(token);
        var secureFlag = secure ? "; Secure" : "";
        return $"{CookieName}={value}; Path=/; HttpOnly; SameSite=Lax{secureFlag}";
    }
}
