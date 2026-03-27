namespace OpenLightFX.Emby.Engine;

using MediaBrowser.Model.Logging;
using OpenLightFX.Emby.Configuration;
using OpenLightFX.Emby.Drivers;
using OpenLightFX.Emby.Effects;
using OpenLightFX.Emby.Models;
using OpenLightFX.Emby.Utilities;
using Openlightfx;
using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// Manages a single playback-to-lighting session. Owns the state machine,
/// keyframe scheduler, channel manager, effect expansion, and bulb dispatch.
/// One instance per active Emby playback session that has a matched .lightfx track.
/// </summary>
public class PlaybackSession
{
    private readonly TrackInfo _trackInfo;
    private readonly LightFXTrack _track;
    private readonly List<BulbConfig> _bulbConfigs;
    private readonly MappingProfile _profile;
    private readonly PluginOptions _options;
    private readonly EffectRendererFactory _effectFactory;
    private readonly ILogger _logger;
    private readonly ChannelManager _channelManager;

    // Active bulb drivers keyed by bulb ID
    private readonly ConcurrentDictionary<string, IBulbDriver> _drivers = new();

    // Pre-sorted keyframes by timestamp for binary search
    private readonly List<Keyframe> _sortedKeyframes;
    private readonly List<EffectKeyframe> _sortedEffects;

    // Playback state
    private PlaybackState _state = PlaybackState.Idle;
    private bool _disposed;

    // Real-time clock: last position synced from Emby + the wall-clock tick at that moment.
    // Between progress events we extrapolate: currentPositionMs = _syncPositionMs + elapsed.
    // Clock is NOT started until the first UpdatePosition() call from Emby.
    private ulong _syncPositionMs;
    private long _syncTimestampTicks;
    private bool _clockStarted;

    // Position as of the last Tick() call — used for seek detection and dispatch throttling
    private ulong _currentPositionMs;
    private ulong _lastDispatchedMs;

    // Per-channel: index of last dispatched keyframe (for forward scanning)
    private readonly Dictionary<string, int> _channelKeyframeIndex = new();

    // Active effect expansions: channelId → list of effect windows with end times
    private readonly Dictionary<string, List<EffectWindow>> _activeEffects = new();

    // Tracks which effect keyframes have already been expanded (key: "{channelId}:{timestampMs}")
    private readonly HashSet<string> _expandedEffects = new();

    // Last-sent command per bulb — used to skip redundant dispatches when state is unchanged
    private readonly Dictionary<string, BulbCommand> _lastSentCommands = new();

    // AI keyframe injection: worker appends to this queue; ProcessKeyframes drains it each tick
    private readonly ConcurrentQueue<Keyframe> _aiKeyframeQueue = new();

    /// <summary>
    /// Raised when a seek is detected. AI lighting worker uses this to reset its analysis cursor.
    /// The argument is the new playback position in milliseconds.
    /// </summary>
    public Action<ulong>? OnSeek { get; set; }

    public PlaybackSession(
        TrackInfo trackInfo,
        List<BulbConfig> bulbConfigs,
        MappingProfile profile,
        PluginOptions options,
        EffectRendererFactory effectFactory,
        ILogger logger)
    {
        _trackInfo = trackInfo;
        _track = trackInfo.Track;
        _bulbConfigs = bulbConfigs;
        _profile = profile;
        _options = options;
        _effectFactory = effectFactory;
        _logger = logger;

        var brightnessCap = (uint)Math.Clamp(options.GlobalBrightnessCap, 0, 100);
        _channelManager = new ChannelManager(_track, profile, brightnessCap);

        // Pre-sort keyframes by (channelId, timestampMs) for efficient lookup
        _sortedKeyframes = _track.Keyframes
            .OrderBy(k => k.ChannelId)
            .ThenBy(k => k.TimestampMs)
            .ToList();

        _sortedEffects = _track.EffectKeyframes
            .OrderBy(e => e.ChannelId)
            .ThenBy(e => e.TimestampMs)
            .ToList();
    }

    public PlaybackState State => _state;
    public ulong CurrentPositionMs => _currentPositionMs;

    /// <summary>
    /// Appends AI-generated keyframes to the session. Thread-safe — called from AiLightingWorker.
    /// Keyframes are merged into the sorted keyframe list on the next Tick().
    /// </summary>
    public void AppendKeyframes(IEnumerable<Keyframe> keyframes)
    {
        foreach (var kf in keyframes)
            _aiKeyframeQueue.Enqueue(kf);
    }

    /// <summary>Initialize drivers and dispatch the first keyframe state (no bare power-on).</summary>
    public async Task StartAsync()
    {
        _state = PlaybackState.Loading;

        // Create drivers for mapped bulbs
        var mappedBulbIds = _channelManager.GetMappedBulbIds().ToHashSet();
        foreach (var config in _bulbConfigs)
        {
            if (!mappedBulbIds.Contains(config.Id)) continue;
            try
            {
                var driver = BulbDriverFactory.Create(config);
                _drivers[config.Id] = driver;
                _logger.Debug("Created driver for bulb '{0}' ({1} @ {2})",
                    config.Name ?? config.Id, config.Protocol, config.IpAddress);
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to create driver for bulb '{0}': {1}", config.Id, ex.Message);
            }
        }

        // Process keyframes at position 0 so the first frame's color is used
        // instead of potentially-unset channel defaults
        ProcessKeyframes(0);

        // Do NOT start the real-time clock here — Emby may still be buffering.
        // The clock starts on the first UpdatePosition() call from Emby.

        // Dispatch the computed state directly — SetState includes "state":true
        // so bulbs power on with the correct color (no white flash from bare SetPower)
        await DispatchCurrentState();

        _state = PlaybackState.Playing;
        _logger.Debug("PlaybackSession started with {0} drivers", _drivers.Count);
    }

    /// <summary>Stop playback and restore/power off bulbs.</summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _state = PlaybackState.Stopped;

        // Apply end behavior
        await ApplyEndBehavior();

        // Dispose all drivers
        foreach (var kv in _drivers)
        {
            try { await kv.Value.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.Warn("Error disposing driver '{0}': {1}", kv.Key, ex.Message);
            }
        }
        _drivers.Clear();

        _logger.Debug("PlaybackSession stopped");
    }

    /// <summary>Pause lighting — freeze the real-time clock and hold current state.</summary>
    public void Pause()
    {
        if (_state != PlaybackState.Playing) return;

        // Snapshot the extrapolated position so the clock doesn't drift during pause
        if (_clockStarted)
        {
            var elapsedMs = (ulong)((Stopwatch.GetTimestamp() - _syncTimestampTicks)
                * 1000.0 / Stopwatch.Frequency);
            _syncPositionMs += elapsedMs;
            _clockStarted = false;
        }

        _state = PlaybackState.Paused;
    }

    /// <summary>Resume lighting after pause — re-anchor the real-time clock.</summary>
    public void Resume()
    {
        if (_state != PlaybackState.Paused) return;

        // Re-anchor the clock at the frozen position
        _syncTimestampTicks = Stopwatch.GetTimestamp();
        _clockStarted = true;
        _state = PlaybackState.Playing;
    }

    /// <summary>Update the current playback position from Emby's progress events.</summary>
    public void UpdatePosition(ulong positionMs)
    {
        // Apply global time offset
        var offset = _options.GlobalTimeOffsetMs;
        var adjusted = offset >= 0
            ? positionMs + (ulong)offset
            : positionMs > (ulong)(-offset) ? positionMs - (ulong)(-offset) : 0;

        // Compute our extrapolated position for comparison
        var extrapolated = _syncPositionMs;
        if (_clockStarted)
        {
            var elapsedMs = (ulong)((Stopwatch.GetTimestamp() - _syncTimestampTicks)
                * 1000.0 / Stopwatch.Frequency);
            extrapolated += elapsedMs;
        }

        if (_clockStarted && (_state == PlaybackState.Playing || _state == PlaybackState.Paused))
        {
            if (adjusted < extrapolated)
            {
                // Emby is reporting a position BEHIND our clock
                var backDelta = extrapolated - adjusted;
                if (backDelta > 5000)
                {
                    // Large backward jump = seek
                    _logger.Debug("Seek detected (backward): {0}ms → {1}ms", extrapolated, adjusted);
                    _channelKeyframeIndex.Clear();
                    _activeEffects.Clear();
                    _expandedEffects.Clear();
                    _lastSentCommands.Clear();
                    _lastDispatchedMs = 0;
                    OnSeek?.Invoke(adjusted); // notify AI worker
                }
                else
                {
                    // Small backward drift — Emby's event is stale, ignore it.
                    // Our Stopwatch-based clock is more accurate for sub-second timing.
                    return;
                }
            }
            else
            {
                // Emby is ahead of or equal to our clock
                var fwdDelta = adjusted - extrapolated;
                if (fwdDelta > 5000)
                {
                    // Large forward jump = seek
                    _logger.Debug("Seek detected (forward): {0}ms → {1}ms", extrapolated, adjusted);
                    _channelKeyframeIndex.Clear();
                    _activeEffects.Clear();
                    _expandedEffects.Clear();
                    _lastSentCommands.Clear();
                    _lastDispatchedMs = 0;
                    OnSeek?.Invoke(adjusted); // notify AI worker
                }
                // Small forward drift or exact match — accept the correction below
            }
        }

        // Sync the real-time clock to the reported position
        _syncPositionMs = adjusted;
        _syncTimestampTicks = Stopwatch.GetTimestamp();
        _clockStarted = true;

        if (_state == PlaybackState.Paused)
            _state = PlaybackState.Playing;
    }

    /// <summary>Called on each poll tick — advance the lighting state.</summary>
    public void Tick()
    {
        if (_state != PlaybackState.Playing) return;

        // Don't extrapolate until Emby has reported the first position
        if (!_clockStarted) return;

        try
        {
            // Extrapolate current position from the last Emby sync point
            var elapsedMs = (ulong)((Stopwatch.GetTimestamp() - _syncTimestampTicks)
                * 1000.0 / Stopwatch.Frequency);
            _currentPositionMs = _syncPositionMs + elapsedMs;

            ProcessKeyframes(_currentPositionMs);
            ProcessEffects(_currentPositionMs);

            // Dispatch to bulbs if enough time has elapsed since last dispatch
            var minInterval = (ulong)Math.Max(_options.PollIntervalMs / 2, 50);
            if (_currentPositionMs - _lastDispatchedMs >= minInterval || _lastDispatchedMs == 0)
            {
                _ = DispatchCurrentState();
                _lastDispatchedMs = _currentPositionMs;
            }
        }
        catch (Exception ex)
        {
            _logger.ErrorException("Error in playback tick", ex);
        }
    }

    // ─── Keyframe processing ───────────────────────────────────────────

    private void ProcessKeyframes(ulong positionMs)
    {
        // Drain AI keyframe queue into the sorted list
        while (_aiKeyframeQueue.TryDequeue(out var aiKf))
        {
            // Binary search for insertion point (sort key: ChannelId then TimestampMs)
            int lo = 0, hi = _sortedKeyframes.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                var m = _sortedKeyframes[mid];
                int cmp = string.Compare(m.ChannelId, aiKf.ChannelId, StringComparison.Ordinal);
                if (cmp == 0) cmp = m.TimestampMs.CompareTo(aiKf.TimestampMs);
                if (cmp < 0) lo = mid + 1; else hi = mid;
            }
            _sortedKeyframes.Insert(lo, aiKf);
        }

        foreach (var channelId in _channelManager.GetChannelIds())
        {
            // Get keyframes for this channel
            var channelKfs = GetChannelKeyframes(channelId);
            if (channelKfs.Count == 0) continue;

            // Find the two keyframes bracketing the current position
            var (prev, next) = FindBracketingKeyframes(channelKfs, positionMs);

            if (next == null) continue;

            // Interpolate
            var state = InterpolationEngine.Interpolate(prev, next, positionMs);
            _channelManager.SetChannelState(channelId, state);
        }
    }

    private void ProcessEffects(ulong positionMs)
    {
        foreach (var channelId in _channelManager.GetChannelIds())
        {
            // Check for active effect windows
            if (_activeEffects.TryGetValue(channelId, out var windows))
            {
                // Find the most recent command from a non-expired effect window
                EffectCommand? activeCmd = null;
                foreach (var window in windows)
                {
                    // Skip this window if the effect's duration has elapsed
                    if (positionMs > window.EffectEndMs)
                        continue;

                    // Search backward for the most recent command at or before position
                    for (int i = window.Commands.Count - 1; i >= 0; i--)
                    {
                        if (window.Commands[i].absoluteMs <= positionMs)
                        {
                            activeCmd = window.Commands[i].cmd;
                            break;
                        }
                    }

                    if (activeCmd != null) break;
                }

                if (activeCmd != null)
                {
                    var state = new InterpolationEngine.InterpolatedState(
                        activeCmd.R, activeCmd.G, activeCmd.B,
                        activeCmd.Brightness, 0, ColorMode.Rgb, true);
                    _channelManager.SetChannelState(channelId, state);
                }

                // Clean up expired effect windows (1s grace period after end)
                windows.RemoveAll(w => w.EffectEndMs + 1000 < positionMs);
                if (windows.Count == 0)
                    _activeEffects.Remove(channelId);
            }

            // Expand upcoming effect keyframes
            var channelEffects = GetChannelEffects(channelId);
            foreach (var efx in channelEffects)
            {
                if (efx.TimestampMs > positionMs + (ulong)_options.LookaheadBufferMs)
                    break;
                if (efx.TimestampMs + efx.DurationMs < positionMs)
                    continue;

                var key = $"{channelId}:{efx.TimestampMs}";
                if (_expandedEffects.Contains(key)) continue; // already expanded
                _expandedEffects.Add(key);

                var renderer = _effectFactory.GetRenderer(efx.EffectType);
                if (renderer == null) continue;

                var context = new EffectContext(
                    BulbCapabilities: new BulbCapabilityProfile(true, true, 2000, 6500, 0, 20),
                    GlobalBrightnessCap: (uint)_options.GlobalBrightnessCap,
                    PhotosensitivityEnabled: _options.PhotosensitivityMode
                );

                var cmds = renderer.Render(efx, context);
                if (_options.PhotosensitivityMode)
                    cmds = PhotosensitivityFilter.Apply(cmds);

                // Convert relative offsets to absolute timestamps
                var absoluteCmds = cmds.Select(c =>
                    (absoluteMs: efx.TimestampMs + c.OffsetMs, cmd: c)).ToList();

                var window = new EffectWindow(
                    efx.TimestampMs + efx.DurationMs,
                    absoluteCmds);

                if (!_activeEffects.ContainsKey(channelId))
                    _activeEffects[channelId] = new();
                _activeEffects[channelId].Add(window);
            }
        }
    }

    // ─── Bulb dispatch ─────────────────────────────────────────────────

    private async Task DispatchCurrentState()
    {
        var tasks = new List<Task>();

        foreach (var bulbId in _channelManager.GetMappedBulbIds())
        {
            if (!_drivers.TryGetValue(bulbId, out var driver)) continue;

            var command = _channelManager.GetBulbCommand(bulbId);
            if (command == null) continue;

            // Skip dispatch if we have no real color data yet (all zeros means
            // no keyframe has been reached). Sending RGB(0,0,0) to Wiz causes
            // it to default to warm white which is not what we want.
            if (command.R == 0 && command.G == 0 && command.B == 0
                && command.Brightness == 0 && command.ColorTemperature == 0)
            {
                _logger.Debug("→ Bulb '{0}': skipping dispatch (no color data yet)", bulbId);
                continue;
            }

            // Skip dispatch if state is unchanged since last send
            if (_lastSentCommands.TryGetValue(bulbId, out var last) && last == command)
                continue;

            tasks.Add(DispatchToBulb(bulbId, driver, command));
            _lastSentCommands[bulbId] = command;
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);
    }

    private async Task DispatchToBulb(string bulbId, IBulbDriver driver, BulbCommand command)
    {
        try
        {
            var caps = driver.GetCapabilities();
            var transitionMs = Math.Max(caps.MinTransitionMs, (uint)(_options.PollIntervalMs / 2));

            if (command.ColorTemperature > 0 && caps.SupportsColorTemp)
            {
                _logger.Debug("→ Bulb '{0}': CT={1}K bright={2} trans={3}ms",
                    bulbId, command.ColorTemperature, command.Brightness, transitionMs);
                await driver.SetColorTemperature(
                    command.ColorTemperature, command.Brightness, transitionMs);
            }
            else if (caps.SupportsRgb)
            {
                _logger.Debug("→ Bulb '{0}': RGB({1},{2},{3}) bright={4} trans={5}ms",
                    bulbId, command.R, command.G, command.B, command.Brightness, transitionMs);
                await driver.SetState(
                    command.R, command.G, command.B, command.Brightness, transitionMs);
            }
            else
            {
                _logger.Debug("→ Bulb '{0}': brightness={1} trans={2}ms",
                    bulbId, command.Brightness, transitionMs);
                await driver.SetBrightness(command.Brightness, transitionMs);
            }
        }
        catch (Exception ex)
        {
            _logger.Debug("Bulb '{0}' dispatch error: {1}", bulbId, ex.Message);
        }
    }

    // ─── End behavior ──────────────────────────────────────────────────

    private async Task ApplyEndBehavior()
    {
        var behavior = _options.EndBehaviorOverride;

        var tasks = _drivers.Select(async kv =>
        {
            try
            {
                switch (behavior)
                {
                    case BehaviorOverride.Leave:
                        // Leave bulbs in their current state
                        break;
                    case BehaviorOverride.Off:
                        await kv.Value.SetPower(false);
                        break;
                    case BehaviorOverride.On:
                        await kv.Value.SetState(255, 255, 255, 100, 500);
                        break;
                    default: // UseTrackDefault — just fade out
                        await kv.Value.SetState(0, 0, 0, 0, 3000);
                        await Task.Delay(3100);
                        await kv.Value.SetPower(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("End behavior error for '{0}': {1}", kv.Key, ex.Message);
            }
        });

        await Task.WhenAll(tasks);
    }

    // ─── Keyframe lookup helpers ───────────────────────────────────────

    private List<Keyframe> GetChannelKeyframes(string channelId)
    {
        // Binary search for the start of this channel's keyframes in the sorted list
        return _sortedKeyframes.Where(k => k.ChannelId == channelId).ToList();
    }

    private List<EffectKeyframe> GetChannelEffects(string channelId)
    {
        return _sortedEffects.Where(e => e.ChannelId == channelId).ToList();
    }

    private (Keyframe? prev, Keyframe? next) FindBracketingKeyframes(
        List<Keyframe> keyframes, ulong positionMs)
    {
        if (keyframes.Count == 0)
            return (null, null);

        // Binary search for the first keyframe at or after positionMs
        int lo = 0, hi = keyframes.Count - 1;
        int nextIdx = keyframes.Count; // default: past the end

        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (keyframes[mid].TimestampMs <= positionMs)
                lo = mid + 1;
            else
            {
                nextIdx = mid;
                hi = mid - 1;
            }
        }

        Keyframe? prev = nextIdx > 0 ? keyframes[nextIdx - 1] : null;
        Keyframe? next = nextIdx < keyframes.Count ? keyframes[nextIdx] : null;

        // If we're past all keyframes, use the last one as "next" for its held state
        if (next == null && prev != null)
            next = prev;

        return (prev, next);
    }
}

/// <summary>Groups an effect's rendered commands with its end time for lifetime tracking.</summary>
internal record EffectWindow(
    ulong EffectEndMs,
    List<(ulong absoluteMs, EffectCommand cmd)> Commands);

public enum PlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Seeking,
    Stopped
}
