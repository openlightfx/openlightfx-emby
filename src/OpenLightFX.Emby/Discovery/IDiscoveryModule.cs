namespace OpenLightFX.Emby.Discovery;

public interface IDiscoveryModule
{
    string Protocol { get; }
    Task<List<DiscoveredBulb>> DiscoverAsync(int timeoutMs, CancellationToken ct);
}
