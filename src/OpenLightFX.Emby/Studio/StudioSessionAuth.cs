using System.Collections.Concurrent;
using System.Security.Cryptography;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Services;

namespace OpenLightFX.Emby.Studio;

/// <summary>What a cookie ticket may be used for. Each scope has its own cookie.</summary>
public enum StudioTicketScope
{
    /// <summary>GET of the embedded Studio page files (scripts and styles the page loads).</summary>
    Assets,

    /// <summary>GET of the edit proxy, which a video element cannot send a header to.</summary>
    ProxyMedia
}

/// <summary>
/// Opaque, expiring tickets that stand in for an Emby access token in a cookie. A ticket is
/// only honoured for the scope it was issued for, only until it expires, and only while the
/// Emby token behind it is still active. Tickets live in memory, so a restart revokes them.
/// </summary>
public static class StudioCookieTickets
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(8);

    /// <summary>How long a replaced ticket keeps working, so in-flight range reads finish.</summary>
    public static readonly TimeSpan RotationGrace = TimeSpan.FromMinutes(2);

    private sealed record Entry(string AccessToken, StudioTicketScope Scope, DateTimeOffset IssuedAt, DateTimeOffset Expires);

    private static readonly ConcurrentDictionary<string, Entry> Tickets = new(StringComparer.Ordinal);

    public static string Issue(string accessToken, StudioTicketScope scope, DateTimeOffset now)
    {
        Prune(now);
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Tickets[ticket] = new Entry(accessToken, scope, now, now + Lifetime);
        return ticket;
    }

    /// <summary>The Emby token behind a live ticket of this scope, or null.</summary>
    public static string? Redeem(string? ticket, StudioTicketScope scope, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(ticket) || !Tickets.TryGetValue(ticket, out var entry))
            return null;
        if (entry.Scope != scope)
            return null;
        if (entry.Expires <= now)
        {
            Tickets.TryRemove(ticket, out _);
            return null;
        }
        return entry.AccessToken;
    }

    /// <summary>
    /// Returns the ticket to set for this token. A ticket younger than half its lifetime is
    /// reused; an older one is replaced and kept alive only for <see cref="RotationGrace"/>.
    /// </summary>
    public static string Rotate(string? presented, string accessToken, StudioTicketScope scope, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(presented)
            && Tickets.TryGetValue(presented, out var entry)
            && entry.Scope == scope
            && entry.Expires > now
            && string.Equals(entry.AccessToken, accessToken, StringComparison.Ordinal))
        {
            if (now - entry.IssuedAt < TimeSpan.FromTicks(Lifetime.Ticks / 2))
                return presented;
            var grace = now + RotationGrace;
            if (entry.Expires > grace)
                Tickets[presented] = entry with { Expires = grace };
        }
        return Issue(accessToken, scope, now);
    }

    public static void Revoke(string? ticket)
    {
        if (!string.IsNullOrEmpty(ticket))
            Tickets.TryRemove(ticket, out _);
    }

    private static void Prune(DateTimeOffset now)
    {
        foreach (var pair in Tickets)
        {
            if (pair.Value.Expires <= now)
                Tickets.TryRemove(pair.Key, out _);
        }
    }
}

/// <summary>
/// A video element and a script tag cannot set X-Emby-Token, so Studio uses scoped cookie
/// tickets instead of putting the access token in URLs. Explicit credentials (header,
/// api_key, Authorization) always win over a cookie.
/// </summary>
public static class StudioSessionAuth
{
    public static string CookieName(StudioTicketScope scope) => scope switch
    {
        StudioTicketScope.Assets => "olfx_studio_page",
        _ => "olfx_studio_media"
    };

    /// <summary>The explicit token if present, else the token behind a live ticket of this scope.</summary>
    public static string? PresentedToken(string? accessToken, string? cookieHeader, StudioTicketScope scope, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(accessToken))
            return accessToken;
        var ticket = StudioAssetAuth.ReadCookie(cookieHeader, CookieName(scope));
        return StudioCookieTickets.Redeem(ticket, scope, now);
    }

    /// <summary>Cookie tickets are only for reads. Anything but GET or HEAD needs explicit credentials.</summary>
    public static bool CookieAllowedFor(string? httpMethod) =>
        string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(httpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

    public static string RequireToken(
        IRequest? request,
        StudioTicketScope scope,
        IAuthenticationRepository authentication,
        IUserManager users)
    {
        var explicitToken = StudioStreamUrls.ReadAccessToken(request);
        var cookieHeader = CookieAllowedFor(request?.Verb) ? request?.Headers?["Cookie"] : null;
        var token = PresentedToken(explicitToken, cookieHeader, scope, DateTimeOffset.UtcNow);
        if (string.IsNullOrEmpty(token) || !IsActive(token, authentication, users))
            throw new SecurityException("Access token is invalid or expired.", SecurityExceptionType.Unauthenticated);
        return token;
    }

    /// <summary>
    /// Sets (or refreshes) the scope's ticket cookie for a request that carried an explicit
    /// token. Requests authenticated only by the cookie do not mint new tickets.
    /// </summary>
    public static void Remember(IRequest? request, StudioTicketScope scope)
    {
        var response = request?.Response;
        var token = StudioStreamUrls.ReadAccessToken(request);
        if (response == null || string.IsNullOrEmpty(token))
            return;
        var name = CookieName(scope);
        var presented = StudioAssetAuth.ReadCookie(request?.Headers?["Cookie"], name);
        var ticket = StudioCookieTickets.Rotate(presented, token, scope, DateTimeOffset.UtcNow);
        response.AddHeader(
            "Set-Cookie",
            StudioAssetAuth.SetCookieHeader(name, ticket, IsSecure(request), StudioCookieTickets.Lifetime));
    }

    /// <summary>HTTPS directly, or HTTPS terminated at a reverse proxy that says so.</summary>
    public static bool IsSecure(IRequest? request)
    {
        if (request == null)
            return false;
        if (request.IsSecureConnection)
            return true;
        var forwarded = request.Headers?["X-Forwarded-Proto"];
        if (!string.IsNullOrEmpty(forwarded)
            && forwarded.Split(',')[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase))
            return true;
        return request.AbsoluteUri?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static bool IsActive(string token, IAuthenticationRepository authentication, IUserManager users)
    {
        try
        {
            var matches = authentication.Get(new AuthenticationInfoQuery
            {
                AccessToken = token,
                IsActive = true,
                Limit = 1
            });

            var info = matches?.Items?.FirstOrDefault(item =>
                item.IsActive && string.Equals(item.AccessToken, token, StringComparison.Ordinal));
            if (info == null)
                return false;
            if (info.UserId == 0)
                return true;

            var user = users.GetUserById(info.UserId);
            return user?.Policy?.IsDisabled == false;
        }
        catch (SecurityException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }
}
