namespace OpenLightFX.Emby.Api;

using System.Collections.Generic;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Security;
using MediaBrowser.Model.Services;
using OpenLightFX.Emby.Studio;

[Route(StudioPaths.RoutePrefix, "GET", Description = "OpenLightFX Studio editor")]
[Route(StudioPaths.RoutePrefix + "/{RelativePath*}", "GET", Description = "OpenLightFX Studio static file")]
[Unauthenticated]
public class GetStudioAsset : IReturnVoid
{
    public string? RelativePath { get; set; }
}

/// <summary>
/// Serves the embedded Studio single-page app. Unknown extensionless paths return index.html.
/// Marked unauthenticated so this method can accept the studio cookie; every response still
/// requires a live Emby access token.
/// </summary>
public class StudioAssetService : IService, IRequiresRequest
{
    private readonly IHttpResultFactory _results;
    private readonly IAuthenticationRepository _authentication;
    private readonly IUserManager _users;
    private readonly StudioAssetCatalog _catalog;

    public IRequest Request { get; set; } = null!;

    public StudioAssetService(
        IHttpResultFactory results,
        IAuthenticationRepository authentication,
        IUserManager users)
        : this(results, authentication, users, StudioAssetCatalog.Shared)
    {
    }

    internal StudioAssetService(
        IHttpResultFactory results,
        IAuthenticationRepository authentication,
        IUserManager users,
        StudioAssetCatalog catalog)
    {
        _results = results;
        _authentication = authentication;
        _users = users;
        _catalog = catalog;
    }

    public object Get(GetStudioAsset request)
    {
        // PathInfo has already lost its trailing slash. RawUrl and AbsoluteUri still
        // reflect the path the browser used, including an /emby prefix from the proxy.
        var redirect = StudioRootRedirect.Target(Request?.RawUrl, Request?.AbsoluteUri, Request?.PathInfo);
        if (redirect != null)
            return _results.GetRedirectResult(redirect);

        var token = RequireToken();
        RememberToken(token);

        var asset = _catalog.Resolve(request.RelativePath);
        if (asset == null)
            throw new ResourceNotFoundException("Studio asset not found");

        var headers = new Dictionary<string, string>
        {
            ["Cache-Control"] = asset.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
                ? "no-cache"
                : "public, max-age=3600",
            ["X-Content-Type-Options"] = "nosniff"
        };

        return _results.GetResult(Request, new ReadOnlyMemory<byte>(asset.Content), asset.ContentType, headers);
    }

    private string RequireToken()
    {
        var token = StudioStreamUrls.ReadAccessToken(Request);
        if (string.IsNullOrEmpty(token))
            token = StudioAssetAuth.ReadCookie(Request?.Headers?["Cookie"], StudioAssetAuth.CookieName);
        if (string.IsNullOrEmpty(token) || !IsActiveToken(token))
            throw new SecurityException("Access token is invalid or expired.", SecurityExceptionType.Unauthenticated);
        return token;
    }

    private bool IsActiveToken(string token)
    {
        try
        {
            var matches = _authentication.Get(new AuthenticationInfoQuery
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

            var user = _users.GetUserById(info.UserId);
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

    private void RememberToken(string token)
    {
        var response = Request?.Response;
        if (response == null)
            return;

        var secure = Request?.IsSecureConnection == true;
        response.AddHeader("Set-Cookie", StudioAssetAuth.SetCookieHeader(token, secure));
    }
}
