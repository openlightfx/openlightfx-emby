using System.Collections.Concurrent;

namespace OpenLightFX.Emby.Studio;

public sealed class StudioSourceWorkspace
{
    private static readonly ConcurrentDictionary<string, StudioSourceWorkspace> Workspaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private string _state = "copying";
    private string _error = "";
    private long _copied;
    private readonly long _length;

    private StudioSourceWorkspace(string itemId, string sourcePath, string directory, string? cachedProxyPath)
    {
        ItemId = itemId;
        SourcePath = sourcePath;
        DirectoryPath = directory;
        TargetPath = Path.Combine(directory, "source" + Path.GetExtension(sourcePath));
        ProxyPath = Path.Combine(directory, "edit-proxy.mp4");
        CachedProxyPath = cachedProxyPath;
        _length = new FileInfo(sourcePath).Length + (cachedProxyPath == null ? 0 : new FileInfo(cachedProxyPath).Length);
    }

    public string ItemId { get; }
    public string SourcePath { get; }
    public string DirectoryPath { get; }
    public string TargetPath { get; }
    public string ProxyPath { get; }
    private string? CachedProxyPath { get; }

    public static StudioSourceWorkspace? Find(string itemId) =>
        Workspaces.TryGetValue(itemId, out var workspace) ? workspace : null;

    public static void PruneAbandoned(string? tempDirectory, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return;
        var root = Path.Combine(tempDirectory, "OpenLightFXStudio");
        if (!Directory.Exists(root)) return;
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddHours(-48);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static StudioSourceWorkspace? Start(string itemId, string sourcePath, string? tempDirectory, string? durableProxyPath)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return null;
        if (Workspaces.TryGetValue(itemId, out var existing))
        {
            if (string.Equals(existing.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)
                && existing.Snapshot().State != "failed") return existing;
            Release(itemId);
        }

        var root = Path.Combine(tempDirectory, "OpenLightFXStudio");
        Directory.CreateDirectory(root);
        var workspaceDirectory = Path.Combine(root, SafeName(itemId) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDirectory);
        var cachedProxy = durableProxyPath != null && StudioProxyJob.IsComplete(durableProxyPath)
            ? durableProxyPath
            : null;
        var created = new StudioSourceWorkspace(itemId, sourcePath, workspaceDirectory, cachedProxy);
        if (!Workspaces.TryAdd(itemId, created))
        {
            try { Directory.Delete(workspaceDirectory, true); } catch { }
            return Workspaces[itemId];
        }
        _ = Task.Run(created.CopyAsync);
        return created;
    }

    public (string State, string Error, long Copied, long Length) Snapshot()
    {
        lock (_gate) return (_state, _error, _copied, _length);
    }

    public string ResolveSource(string fallback) => Snapshot().State == "ready" ? TargetPath : fallback;

    public static void Release(string itemId)
    {
        if (!Workspaces.TryRemove(itemId, out var workspace)) return;
        var state = workspace.Snapshot().State;
        if (state == "copying")
        {
            lock (workspace._gate) workspace._cancellation.Cancel();
            _ = Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                DeleteDirectory(workspace.DirectoryPath);
            });
            return;
        }
        if (StudioProxyCache.TryGet(itemId, out var proxy) && proxy.State == "building"
            || SceneCutActivity.IsRunning(itemId))
        {
            _ = Task.Run(async () =>
            {
                while ((StudioProxyCache.TryGet(itemId, out var runningProxy) && runningProxy.State == "building")
                    || SceneCutActivity.IsRunning(itemId))
                    await Task.Delay(250).ConfigureAwait(false);
                DeleteDirectory(workspace.DirectoryPath);
            });
            return;
        }
        DeleteDirectory(workspace.DirectoryPath);
    }

    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async Task CopyAsync()
    {
        try
        {
            await CopyFileAsync(SourcePath, TargetPath + ".copying", async () =>
            {
                lock (_gate)
                {
                    _cancellation.Token.ThrowIfCancellationRequested();
                    File.Move(TargetPath + ".copying", TargetPath, true);
                }
                if (CachedProxyPath == null)
                {
                    lock (_gate) { _copied = _length; _state = "ready"; }
                    return;
                }
                try
                {
                    await CopyFileAsync(CachedProxyPath, ProxyPath + ".copying", () =>
                    {
                        lock (_gate)
                        {
                            _cancellation.Token.ThrowIfCancellationRequested();
                            File.Move(ProxyPath + ".copying", ProxyPath, true);
                            File.WriteAllText(StudioProxyJob.CompleteMarker(ProxyPath), StudioProxyPlan.CacheVersion);
                            _copied = _length;
                            _state = "ready";
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    try { if (File.Exists(ProxyPath + ".copying")) File.Delete(ProxyPath + ".copying"); } catch { }
                    lock (_gate) { _copied = _length; _state = "ready"; }
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); } catch { }
        }
        catch
        {
            try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); } catch { }
            lock (_gate) { _state = "failed"; _error = "The movie could not be copied to the transcode temporary directory. Check that the Emby service account can read, write, and delete files there."; }
        }
    }

    private async Task CopyFileAsync(string source, string destination, Func<Task> afterOutputClosed)
    {
        await using var input = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (var output = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = await input.ReadAsync(buffer, _cancellation.Token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), _cancellation.Token).ConfigureAwait(false);
                lock (_gate) _copied += read;
            }
            await output.FlushAsync(_cancellation.Token).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        await afterOutputClosed().ConfigureAwait(false);
    }

    private static string SafeName(string value) => string.Concat(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

public sealed class StudioSourceWorkspaceResponse
{
    [System.Runtime.Serialization.DataMember(Name = "required")] public bool Required { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "state")] public string State { get; set; } = "ready";
    [System.Runtime.Serialization.DataMember(Name = "error")] public string Error { get; set; } = "";
    [System.Runtime.Serialization.DataMember(Name = "copiedBytes")] public long CopiedBytes { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "totalBytes")] public long TotalBytes { get; set; }
    [System.Runtime.Serialization.DataMember(Name = "progressPercent")] public int ProgressPercent => State == "ready" ? 100 : TotalBytes <= 0 ? 0 : (int)Math.Clamp(CopiedBytes * 100 / TotalBytes, 0, 99);
}
