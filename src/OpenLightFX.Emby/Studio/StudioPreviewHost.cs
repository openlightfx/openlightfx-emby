using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Models;

namespace OpenLightFX.Emby.Studio;

/// <summary>
/// One Studio preview at a time. Playback and preview share <see cref="ExclusiveAsync"/>
/// so they never own bulb drivers together.
/// </summary>
public sealed class StudioPreviewHost : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sessionLock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private StudioPreviewSession? _session;
    private Dictionary<string, List<string>> _channelToBulbs = new(StringComparer.Ordinal);
    private int _disposed;

    public StudioPreviewHost(bool startPump = true)
    {
        _pump = startPump ? PumpLoop(_cts.Token) : Task.CompletedTask;
    }

    public bool IsActive
    {
        get
        {
            lock (_sessionLock)
                return _session is { IsActive: true };
        }
    }

    public async Task ExclusiveAsync(Func<Task> action)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryStartAsync(
        Func<bool> playbackActive,
        bool takeover,
        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping,
        IReadOnlyList<BulbConfig> bulbs,
        bool photosensitivity,
        Func<BulbConfig, IBulbDriver> createDriver,
        DateTimeOffset now)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (playbackActive() && !takeover)
                return false;

            await StopCoreAsync().ConfigureAwait(false);
            var session = await StudioPreviewSession.CreateAsync(
                mapping, bulbs, photosensitivity, createDriver, now).ConfigureAwait(false);
            lock (_sessionLock)
            {
                _session = session;
                _channelToBulbs = mapping.ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value.Distinct(StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SubmitFrameAsync(IReadOnlyList<StudioPreviewSample> samples, DateTimeOffset now)
    {
        var session = CurrentSession();
        if (session == null)
            throw new StudioPreviewNotActiveException();
        await session.SubmitFrameAsync(samples, now).ConfigureAwait(false);
    }

    public async Task SubmitChannelFrameAsync(IReadOnlyList<StudioPreviewChannelSample> samples, DateTimeOffset now)
    {
        var session = CurrentSession();
        if (session == null)
            throw new StudioPreviewNotActiveException();

        Dictionary<string, List<StudioPreviewChannelSample>> byBulb = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> mapping;
        lock (_sessionLock)
            mapping = _channelToBulbs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            if (!mapping.TryGetValue(sample.ChannelId, out var bulbIds)) continue;
            foreach (var bulbId in bulbIds)
            {
                if (!byBulb.TryGetValue(bulbId, out var list)) byBulb[bulbId] = list = new();
                list.Add(sample);
            }
        }

        var bulbSamples = byBulb.Select(pair =>
        {
            var sources = pair.Value.Select(sample => new OpenLightFX.Emby.Engine.InterpolationEngine.InterpolatedState(
                (byte)Math.Clamp(sample.R, 0, 255),
                (byte)Math.Clamp(sample.G, 0, 255),
                (byte)Math.Clamp(sample.B, 0, 255),
                (uint)Math.Clamp(sample.Brightness, 0, 100),
                0,
                Openlightfx.ColorMode.Rgb,
                sample.Brightness > 0));
            var mixed = OpenLightFX.Emby.Rooms.BulbMixer.Mix(sources.ToArray());
            return new StudioPreviewSample(
                pair.Key,
                mixed.R,
                mixed.G,
                mixed.B,
                (int)mixed.Brightness,
                pair.Value.Max(sample => Math.Max(sample.TransitionMs, 0)));
        }).ToArray();
        await session.SubmitFrameAsync(bulbSamples, now).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller must already hold <see cref="ExclusiveAsync"/>.</summary>
    public Task StopCoreAsync()
    {
        StudioPreviewSession? session;
        lock (_sessionLock)
        {
            session = _session;
            _session = null;
            _channelToBulbs.Clear();
        }

        return session?.StopAsync() ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* pump already finished */ }

        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _gate.Dispose();
    }

    private StudioPreviewSession? CurrentSession()
    {
        lock (_sessionLock)
            return _session is { IsActive: true } ? _session : null;
    }

    private async Task PumpLoop(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                StudioPreviewSession? session;
                lock (_sessionLock)
                    session = _session;

                if (session == null)
                    continue;

                try
                {
                    await session.PumpAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
                }
                catch
                {
                    // A failed frame must not kill the pump. The next tick retries or idle-stops.
                }

                if (!session.IsActive)
                {
                    lock (_sessionLock)
                    {
                        if (ReferenceEquals(_session, session))
                            _session = null;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }
}
