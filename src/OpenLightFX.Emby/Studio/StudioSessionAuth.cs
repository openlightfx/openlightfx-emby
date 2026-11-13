using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Services;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// A video element cannot set X-Emby-Token. Studio's HttpOnly cookie is sent on
/// same-origin media requests in Firefox and Chromium, so the edit URL stays free
/// of the access token.
/// </summary>
public static class StudioSessionAuth
{
    public static string? PresentedToken(string? accessToken, string? cookieHeader)
    {
        if (!string.IsNullOrEmpty(accessToken))
            return accessToken;
        return StudioAssetAuth.ReadCookie(cookieHeader, StudioAssetAuth.CookieName);
    }

    public static string RequireToken(
        IRequest? request,
        IAuthenticationRepository authentication,
        IUserManager users)
    {
        var token = PresentedToken(
            StudioStreamUrls.ReadAccessToken(request),
            request?.Headers?["Cookie"]);
        if (string.IsNullOrEmpty(token) || !IsActive(token, authentication, users))
            throw new SecurityException("Access token is invalid or expired.", SecurityExceptionType.Unauthenticated);
        return token;
    }

    public static void Remember(IRequest? request, string? token)
    {
        var response = request?.Response;
        if (response == null || string.IsNullOrEmpty(token))
            return;
        response.AddHeader(
            "Set-Cookie",
            StudioAssetAuth.SetCookieHeader(token, request?.IsSecureConnection == true));
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
