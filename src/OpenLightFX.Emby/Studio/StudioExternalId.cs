namespace OpenLightFX.Emby.Studio;

using System.Collections.Concurrent;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

/// <summary>
/// Movie details Links row entry. Emby shows <see cref="IExternalId"/> when the item's
/// provider id is set, formatting <see cref="UrlFormatString"/> with that value.
/// </summary>
public class StudioExternalId : IExternalId
{
    public const string ProviderKey = "OpenLightFX";

    private static readonly ConcurrentDictionary<Guid, byte> Writes = new();

    public string Name => "OpenLightFX Studio";

    public string Key => ProviderKey;

    /// <summary>
    /// <c>{0}</c> is the provider id: the movie's numeric Emby id, the same value
    /// Studio's library uses. Emby's provider-id reader splits values on <c>/</c>.
    /// </summary>
    public string UrlFormatString => StudioPaths.PublicUrl + "#/movie/{0}";

    public bool Supports(IHasProviderIds item) => item is Movie;

    /// <summary>
    /// Id Studio's library and <c>#/movie/{itemId}</c> use. Emby exposes
    /// <see cref="BaseItem.InternalId"/> as the API item id when internal ids are enabled.
    /// </summary>
    public static string PublicId(BaseItem item) =>
        item.InternalId > 0
            ? item.InternalId.ToString(CultureInfo.InvariantCulture)
            : item.Id.ToString("N");

    /// <summary>
    /// Numeric id for the Links row. Null until Emby has assigned <see cref="BaseItem.InternalId"/>,
    /// so a guid is never written in its place.
    /// </summary>
    public static string? MovieLinkId(BaseItem item) =>
        item is Movie && item.InternalId > 0
            ? item.InternalId.ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Sets the provider id when it is missing or not the numeric Emby id.
    /// A stored guid is replaced. Returns false when no write is needed, so a
    /// follow-up <c>ItemUpdated</c> does not save again.
    /// </summary>
    public static bool TryAssign(BaseItem item)
    {
        var id = MovieLinkId(item);
        if (id == null)
            return false;

        if (string.Equals(item.GetProviderId(ProviderKey), id, StringComparison.Ordinal))
            return false;

        item.SetProviderId(ProviderKey, id);
        return true;
    }

    /// <summary>
    /// Persists the provider id when it was missing or wrong. A nested
    /// <c>ItemUpdated</c> sees the id already set and does not save again.
    /// </summary>
    public static bool Ensure(BaseItem item, ILibraryManager library, BaseItem? parent = null)
    {
        if (!TryAssign(item))
            return false;
        if (!Writes.TryAdd(item.Id, 0))
            return false;

        try
        {
            library.UpdateItem(item, parent, ItemUpdateType.MetadataEdit);
            return true;
        }
        finally
        {
            Writes.TryRemove(item.Id, out _);
        }
    }
}
