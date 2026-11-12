using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Effects;
using OpenLightFX.Emby.Models;

namespace OpenLightFX.Emby.Studio;

public sealed class StudioPreviewNotActiveException : InvalidOperationException
{
    public StudioPreviewNotActiveException()
        : base("Preview is not active")
    {
    }
}

public readonly record struct StudioPreviewSample(
    string BulbId,
    int R,
    int G,
    int B,
    int Brightness,
    int TransitionMs);

public sealed record StudioPreviewBulb(
    string BulbId,
    IBulbDriver Driver,
    BulbCapabilityProfile Capabilities,
    BulbState? PriorState);

/// <summary>
/// Drives real bulbs for the Studio editor. Frames are coalesced per bulb, rate-limited
/// to the driver's minimum transition and command rate, and optionally passed through
/// <see cref="PhotosensitivityFilter"/>. Idle previews restore the captured bulb state.
/// </summary>
public sealed class StudioPreviewSession : IAsyncDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly Dictionary<string, Lane> _lanes;
    private readonly bool _photosensitivity;
    private readonly DateTimeOffset _startedAt;
    private DateTimeOffset _lastInputAt;
    private bool _active = true;

    private sealed class Lane
    {
        public required IBulbDriver Driver { get; init; }
        public required BulbCapabilityProfile Capabilities { get; init; }
        public BulbState? Prior { get; init; }
        public required TimeSpan Interval { get; init; }
        public List<EffectCommand> Raw { get; } = new();
        public EffectCommand? Pending { get; set; }
        public DateTimeOffset NextAllowed { get; set; }
    }

    private StudioPreviewSession(
        IReadOnlyList<StudioPreviewBulb> bulbs,
        bool photosensitivity,
        DateTimeOffset startedAt)
    {
        _photosensitivity = photosensitivity;
        _startedAt = startedAt;
        _lastInputAt = startedAt;
        _lanes = new Dictionary<string, Lane>(StringComparer.OrdinalIgnoreCase);
        foreach (var bulb in bulbs)
        {
            _lanes[bulb.BulbId] = new Lane
            {
                Driver = bulb.Driver,
                Capabilities = bulb.Capabilities,
                Prior = bulb.PriorState,
                Interval = MinSendInterval(bulb.Capabilities),
                NextAllowed = startedAt
            };
        }
    }

    public bool IsActive => _active;

    public static TimeSpan MinSendInterval(BulbCapabilityProfile caps)
    {
        var fromRateMs = caps.MaxCommandsPerSecond == 0
            ? 1000d
            : Math.Ceiling(1000d / caps.MaxCommandsPerSecond);
        var fromTransitionMs = caps.MinTransitionMs;
        return TimeSpan.FromMilliseconds(Math.Max(fromRateMs, fromTransitionMs));
    }

    public static async Task<StudioPreviewSession> CreateAsync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping,
        IReadOnlyList<BulbConfig> bulbs,
        bool photosensitivity,
        Func<BulbConfig, IBulbDriver> createDriver,
        DateTimeOffset now)
    {
        if (mapping.Count == 0)
            throw new ArgumentException("Preview mapping is empty");

        var byId = new Dictionary<string, BulbConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var bulb in bulbs)
        {
            if (!string.IsNullOrEmpty(bulb.Id))
                byId[bulb.Id] = bulb;
        }

        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channelId, bulbIds) in mapping)
        {
            if (string.IsNullOrWhiteSpace(channelId))
                throw new ArgumentException("Preview mapping channel id is empty");
            if (bulbIds == null || bulbIds.Count == 0)
                throw new ArgumentException($"Preview mapping for channel '{channelId}' has no bulbs");

            foreach (var bulbId in bulbIds)
            {
                if (string.IsNullOrWhiteSpace(bulbId))
                    throw new ArgumentException("Preview mapping contains an empty bulb id");
                if (!byId.ContainsKey(bulbId))
                    throw new ArgumentException($"Unknown bulb '{bulbId}'");
                if (seen.Add(bulbId))
                    wanted.Add(bulbId);
            }
        }

        var opened = new List<StudioPreviewBulb>();
        try
        {
            foreach (var bulbId in wanted)
            {
                var config = byId[bulbId];
                var driver = createDriver(config);
                BulbCapabilityProfile caps;
                try
                {
                    caps = StudioBulbCapabilities.ApplyOverrides(driver.GetCapabilities(), config.CapabilityOverrides);
                }
                catch
                {
                    caps = StudioBulbCapabilities.ApplyOverrides(
                        StudioBulbCapabilities.ForProtocol(config.Protocol),
                        config.CapabilityOverrides);
                }

                BulbState? prior = null;
                try
                {
                    prior = await driver.GetCurrentState().ConfigureAwait(false);
                }
                catch
                {
                    prior = null;
                }

                opened.Add(new StudioPreviewBulb(bulbId, driver, caps, prior));
            }
        }
        catch
        {
            foreach (var openedBulb in opened)
            {
                try { await openedBulb.Driver.DisposeAsync().ConfigureAwait(false); }
                catch { /* best effort */ }
            }
            throw;
        }

        return new StudioPreviewSession(opened, photosensitivity, now);
    }

    public async Task SubmitFrameAsync(IReadOnlyList<StudioPreviewSample> samples, DateTimeOffset now)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_active)
                throw new StudioPreviewNotActiveException();

            _lastInputAt = now;
            var due = new List<(Lane Lane, EffectCommand Command)>();
            foreach (var sample in samples)
            {
                if (string.IsNullOrEmpty(sample.BulbId) || !_lanes.TryGetValue(sample.BulbId, out var lane))
                    continue;

                var command = Shape(lane, sample, now);
                if (now >= lane.NextAllowed)
                {
                    lane.Pending = null;
                    lane.NextAllowed = now + lane.Interval;
                    Commit(lane, command);
                    due.Add((lane, command));
                }
                else
                {
                    // Latest frame wins until the bulb can accept another command.
                    lane.Pending = command;
                }
            }

            foreach (var (lane, command) in due)
                await SendAsync(lane, command).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task PumpAsync(DateTimeOffset now)
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_active)
                return;

            if (now - _lastInputAt >= IdleTimeout)
            {
                await StopHoldingLockAsync().ConfigureAwait(false);
                return;
            }

            var due = new List<(Lane Lane, EffectCommand Command)>();
            foreach (var lane in _lanes.Values)
            {
                if (lane.Pending == null || now < lane.NextAllowed)
                    continue;

                var command = lane.Pending;
                lane.Pending = null;
                lane.NextAllowed = now + lane.Interval;
                Commit(lane, command);
                due.Add((lane, command));
            }

            foreach (var (lane, command) in due)
                await SendAsync(lane, command).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task StopAsync()
    {
        await _io.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopHoldingLockAsync().ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _io.Dispose();
    }

    private async Task StopHoldingLockAsync()
    {
        if (!_active)
            return;

        _active = false;
        var lanes = _lanes.Values.ToList();
        _lanes.Clear();

        foreach (var lane in lanes)
        {
            try { await RestoreAsync(lane).ConfigureAwait(false); }
            catch { /* a dark bulb is better than a stuck preview color */ }

            try { await lane.Driver.DisposeAsync().ConfigureAwait(false); }
            catch { /* already closed */ }
        }
    }

    private EffectCommand Shape(Lane lane, StudioPreviewSample sample, DateTimeOffset now)
    {
        var elapsed = Math.Clamp((now - _startedAt).TotalMilliseconds, 0, uint.MaxValue);
        var minTransition = lane.Capabilities.MinTransitionMs;
        var requested = (uint)Math.Clamp(sample.TransitionMs, 0, int.MaxValue);
        var candidate = new EffectCommand(
            OffsetMs: (uint)elapsed,
            R: (byte)Math.Clamp(sample.R, 0, 255),
            G: (byte)Math.Clamp(sample.G, 0, 255),
            B: (byte)Math.Clamp(sample.B, 0, 255),
            Brightness: (uint)Math.Clamp(sample.Brightness, 0, 100),
            TransitionMs: Math.Max(requested, minTransition));

        // Shape against commands already sent. Pending frames are not committed,
        // so a coalesced burst is filtered as one change from the last real state.
        var window = new List<EffectCommand>(lane.Raw) { candidate };
        while (window.Count > 1 && candidate.OffsetMs - window[0].OffsetMs > 2000)
            window.RemoveAt(0);

        var shaped = candidate;
        if (_photosensitivity)
        {
            var filtered = PhotosensitivityFilter.Apply(window);
            if (filtered.Count > 0)
                shaped = filtered[^1];
        }

        if (shaped.TransitionMs < minTransition)
            shaped = shaped with { TransitionMs = minTransition };

        return shaped;
    }

    private static void Commit(Lane lane, EffectCommand command)
    {
        lane.Raw.Add(command);
        while (lane.Raw.Count > 1 && command.OffsetMs - lane.Raw[0].OffsetMs > 2000)
            lane.Raw.RemoveAt(0);
    }

    private static async Task SendAsync(Lane lane, EffectCommand command)
    {
        if (lane.Capabilities.SupportsRgb)
        {
            await lane.Driver.SetState(
                command.R, command.G, command.B, command.Brightness, command.TransitionMs)
                .ConfigureAwait(false);
        }
        else
        {
            await lane.Driver.SetBrightness(command.Brightness, command.TransitionMs)
                .ConfigureAwait(false);
        }
    }

    private static async Task RestoreAsync(Lane lane)
    {
        var prior = lane.Prior;
        if (prior == null || !prior.IsOn)
        {
            await lane.Driver.SetPower(false).ConfigureAwait(false);
            return;
        }

        if (prior.ColorTemperature is uint kelvin && kelvin > 0 && lane.Capabilities.SupportsColorTemp)
        {
            await lane.Driver.SetColorTemperature(kelvin, prior.Brightness, 300).ConfigureAwait(false);
            return;
        }

        if (lane.Capabilities.SupportsRgb)
        {
            await lane.Driver.SetState(prior.R, prior.G, prior.B, prior.Brightness, 300).ConfigureAwait(false);
            return;
        }

        await lane.Driver.SetBrightness(prior.Brightness, 300).ConfigureAwait(false);
    }
}
