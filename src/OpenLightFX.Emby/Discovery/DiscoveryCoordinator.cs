namespace OpenLightFX.Emby.Discovery;

public class DiscoveryCoordinator
{
    private readonly List<IDiscoveryModule> _modules;

    public DiscoveryCoordinator()
    {
        _modules = new List<IDiscoveryModule>
        {
            new WizDiscovery(),
            new HueDiscovery(),
            new LifxDiscovery(),
            new GoveeDiscovery()
        };
    }

    /// <summary>
    /// Run discovery across specified (or all) protocols concurrently.
    /// </summary>
    public async Task<List<DiscoveredBulb>> DiscoverAsync(
        IEnumerable<string>? protocols = null,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        var modules = protocols != null
            ? _modules.Where(m => protocols.Any(p =>
                string.Equals(p, m.Protocol, StringComparison.OrdinalIgnoreCase))).ToList()
            : _modules;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs + 2000); // 2s overhead

        var tasks = modules.Select(m => SafeDiscover(m, timeoutMs, cts.Token));
        var results = await Task.WhenAll(tasks);

        var allBulbs = results.SelectMany(r => r).ToList();

        // Deduplicate by MAC address or IP
        return DeduplicateBulbs(allBulbs);
    }

    private static async Task<List<DiscoveredBulb>> SafeDiscover(
        IDiscoveryModule module, int timeoutMs, CancellationToken ct)
    {
        try
        {
            return await module.DiscoverAsync(timeoutMs, ct);
        }
        catch
        {
            return new List<DiscoveredBulb>();
        }
    }

    private static List<DiscoveredBulb> DeduplicateBulbs(List<DiscoveredBulb> bulbs)
    {
        var seen = new HashSet<string>();
        var result = new List<DiscoveredBulb>();

        foreach (var bulb in bulbs)
        {
            // Deduplicate by MAC address first, then by IP+Protocol
            var key = !string.IsNullOrEmpty(bulb.MacAddress)
                ? $"mac:{bulb.MacAddress.ToUpperInvariant()}"
                : $"ip:{bulb.IpAddress}:{bulb.Protocol}";

            if (seen.Add(key))
                result.Add(bulb);
        }

        return result;
    }
}
