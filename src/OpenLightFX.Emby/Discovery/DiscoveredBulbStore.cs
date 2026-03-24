namespace OpenLightFX.Emby.Discovery;

using System.Collections.Concurrent;

/// <summary>
/// In-memory store for discovered bulbs from the most recent scan.
/// Cleared on new scan or after 30 minutes of no discovery-related API calls.
/// </summary>
public class DiscoveredBulbStore
{
    private readonly ConcurrentDictionary<string, DiscoveredBulb> _bulbs = new();
    private DateTime _lastAccessTime = DateTime.UtcNow;
    private static readonly TimeSpan ExpirationTimeout = TimeSpan.FromMinutes(30);

    public void Clear()
    {
        _bulbs.Clear();
        _lastAccessTime = DateTime.UtcNow;
    }

    public void AddOrUpdate(DiscoveredBulb bulb)
    {
        _bulbs[bulb.Id] = bulb;
        _lastAccessTime = DateTime.UtcNow;
    }

    public void AddRange(IEnumerable<DiscoveredBulb> bulbs)
    {
        foreach (var bulb in bulbs)
            _bulbs[bulb.Id] = bulb;
        _lastAccessTime = DateTime.UtcNow;
    }

    public DiscoveredBulb? Get(string bulbId)
    {
        CheckExpiration();
        _lastAccessTime = DateTime.UtcNow;
        return _bulbs.TryGetValue(bulbId, out var bulb) ? bulb : null;
    }

    public IReadOnlyList<DiscoveredBulb> GetAll()
    {
        CheckExpiration();
        _lastAccessTime = DateTime.UtcNow;
        return _bulbs.Values.ToList();
    }

    public int Count => _bulbs.Count;

    private void CheckExpiration()
    {
        if (DateTime.UtcNow - _lastAccessTime > ExpirationTimeout)
            _bulbs.Clear();
    }
}
