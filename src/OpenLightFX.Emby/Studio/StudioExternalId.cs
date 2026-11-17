namespace OpenLightFX.Emby.Studio;

using System.Collections.Concurrent;
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
    /// <c>{0}</c> is the provider id. Emby's provider-id reader splits values on
    /// <c>/</c>, so the id itself is the movie guid with no slashes.
    /// </summary>
    public string UrlFormatString => StudioPaths.PublicUrl + "#/movie/{0}";

    public bool Supports(IHasProviderIds item) => item is Movie;

    /// <summary>
    /// Guid format <c>N</c> (32 hex digits). Emby's API serializes item ids that way,
    /// and Studio's <c>#/movie/{itemId}</c> route requires an exact match with <c>Id</c>.
    /// </summary>
    public static string MovieId(BaseItem item) => item.Id.ToString("N");

    /// <summary>
    /// Sets the provider id when it is missing or different. Returns false when the
    /// movie already points at itself, so a follow-up <c>ItemUpdated</c> does not save again.
    /// </summary>
    public static bool TryAssign(BaseItem item)
    {
        if (item is not Movie || item.Id == Guid.Empty)
            return false;

        var id = MovieId(item);
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
