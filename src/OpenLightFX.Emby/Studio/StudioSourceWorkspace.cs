using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace OpenLightFX.Emby.Studio;

public sealed class StudioSourceWorkspace
{
    private static readonly ConcurrentDictionary<string, StudioSourceWorkspace> Workspaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object WorkspacesGate = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation = new();
    private long _leaseGeneration = 1;
    private readonly string _copyId = Guid.NewGuid().ToString("N");
    private readonly string _sourceCopyPath;
    private readonly string _proxyCopyPath;
    private string _state = "copying";
    private string _error = "";
    private long _copied;
    private readonly long _sourceLength;
    private readonly long _length;
    private readonly bool _proxyOnly;

    private StudioSourceWorkspace(
        string itemId,
        string sourcePath,
        string directory,
        string? cachedProxyPath,
        bool proxyOnly,
        long sourceLength,
        long proxyLength,
        bool sourceCopyReady,
        bool proxyCopyReady)
    {
        ItemId = itemId;
        SourcePath = sourcePath;
        DirectoryPath = directory;
        TargetPath = Path.Combine(directory, "source" + Path.GetExtension(sourcePath));
        ProxyPath = Path.Combine(directory, "edit-proxy.mp4");
        CachedProxyPath = cachedProxyPath;
        _proxyOnly = proxyOnly;
        _sourceLength = sourceLength;
        _length = proxyOnly ? proxyLength : sourceLength + (cachedProxyPath == null ? 0 : proxyLength);
        _sourceCopyPath = TargetPath + "." + _copyId + ".copying";
        _proxyCopyPath = ProxyPath + "." + _copyId + ".copying";
        _copied = (!proxyOnly && sourceCopyReady ? sourceLength : 0)
            + (proxyCopyReady && cachedProxyPath != null ? proxyLength : 0);
        if (sourceCopyReady && (proxyOnly ? proxyCopyReady : cachedProxyPath == null || proxyCopyReady))
            _state = "ready";
    }

    public string ItemId { get; }
    public string SourcePath { get; }
    public string DirectoryPath { get; }
    public string TargetPath { get; }
    public string ProxyPath { get; }
    public bool ProxyOnly => _proxyOnly;
    private string? CachedProxyPath { get; }

    public static StudioSourceWorkspace? Find(string itemId) =>
        Workspaces.TryGetValue(itemId, out var workspace) ? workspace : null;

    public static void PruneAbandoned(string? tempDirectory, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return;
        var root = Path.Combine(tempDirectory, "OpenLightFXStudio");
        if (!Directory.Exists(root)) return;
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddHours(-48);
        string[] activeDirectories;
        lock (WorkspacesGate)
            activeDirectories = Workspaces.Values.Select(w => Path.GetFullPath(w.DirectoryPath)).ToArray();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var fullPath = Path.GetFullPath(path);
            if (activeDirectories.Any(directory => IsWithinDirectory(fullPath, directory))) continue;
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

    public static StudioSourceWorkspace? Start(string itemId, string sourcePath, string? tempDirectory, string? durableProxyPath, bool proxyOnly = false)
    {
        if (string.IsNullOrWhiteSpace(tempDirectory)) return null;

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var sourceInfo = new FileInfo(fullSourcePath);
        var sourceLength = sourceInfo.Length;
        var workspaceKey = WorkspaceKey(itemId, fullSourcePath, sourceLength, sourceInfo.LastWriteTimeUtc.Ticks);
        var root = Path.Combine(tempDirectory, "OpenLightFXStudio");
        var workspaceDirectory = Path.Combine(root, workspaceKey);
        var cachedProxy = durableProxyPath != null && StudioProxyJob.IsComplete(durableProxyPath)
            ? durableProxyPath
            : null;
        proxyOnly = proxyOnly && cachedProxy != null;
        var proxyLength = cachedProxy == null ? 0 : new FileInfo(cachedProxy).Length;

        lock (WorkspacesGate)
        {
            if (Workspaces.TryGetValue(itemId, out var existing))
            {
                if (PathsEqual(existing.DirectoryPath, workspaceDirectory)
                    && existing.Snapshot().State != "failed")
                {
                    existing.RenewLease();
                    return existing;
                }
                Workspaces.TryRemove(itemId, out _);
                existing.CancelIfCopying();
            }

            Directory.CreateDirectory(workspaceDirectory);
            var targetPath = Path.Combine(workspaceDirectory, "source" + Path.GetExtension(fullSourcePath));
            var proxyPath = Path.Combine(workspaceDirectory, "edit-proxy.mp4");
            var sourceCopyReady = proxyOnly || IsSourceCopyValid(targetPath, sourceLength);
            var proxyCopyReady = StudioProxyJob.IsComplete(proxyPath);
            if (sourceCopyReady) Touch(targetPath);
            if (proxyCopyReady)
            {
                Touch(proxyPath);
                Touch(StudioProxyJob.CompleteMarker(proxyPath));
            }

            var created = new StudioSourceWorkspace(
                itemId, fullSourcePath, workspaceDirectory, cachedProxy, proxyOnly,
                sourceLength, proxyLength, sourceCopyReady, proxyCopyReady);
            Workspaces[itemId] = created;
            if (created.Snapshot().State != "ready") _ = Task.Run(created.CopyAsync);
            return created;
        }
    }

    public (string State, string Error, long Copied, long Length) Snapshot()
    {
        lock (_gate) return (_state, _error, _copied, _length);
    }

    public string ResolveSource(string fallback) =>
        Snapshot().State == "ready" && !_proxyOnly ? TargetPath : fallback;

    public static void Release(string itemId)
    {
        StudioSourceWorkspace? workspace;
        lock (WorkspacesGate)
        {
            if (!Workspaces.TryGetValue(itemId, out workspace)) return;
            if (HasBackgroundWork(itemId))
            {
                var releasedLease = workspace.MarkLeaseReleased();
                _ = RetireAfterBackgroundWorkAsync(itemId, workspace, releasedLease);
                return;
            }
            Workspaces.TryRemove(itemId, out _);
        }

        // A completed copy is a reusable cache. If a copy is still in progress,
        // cancel only its uniquely named staging files; never remove a cache that
        // another editor load may already be using.
        workspace.CancelIfCopying();
        if (workspace.Snapshot().State == "failed") DeleteDirectory(workspace.DirectoryPath);
    }

    private static bool HasBackgroundWork(string itemId) =>
        (StudioProxyCache.TryGet(itemId, out var proxy) && proxy.State == "building")
        || SceneCutActivity.IsRunning(itemId);

    private static async Task RetireAfterBackgroundWorkAsync(
        string itemId, StudioSourceWorkspace workspace, long releasedLease)
    {
        while (HasBackgroundWork(itemId)) await Task.Delay(250).ConfigureAwait(false);
        Touch(workspace.TargetPath);
        Touch(workspace.ProxyPath);
        Touch(StudioProxyJob.CompleteMarker(workspace.ProxyPath));
        await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        var retired = false;
        lock (WorkspacesGate)
        {
            if (Workspaces.TryGetValue(itemId, out var current)
                && ReferenceEquals(current, workspace)
                && workspace.LeaseGeneration == releasedLease
                && !HasBackgroundWork(itemId))
                retired = Workspaces.TryRemove(itemId, out _);
        }
        if (retired) workspace.CancelIfCopying();
    }

    private long LeaseGeneration => Interlocked.Read(ref _leaseGeneration);

    private void RenewLease() => Interlocked.Increment(ref _leaseGeneration);

    private long MarkLeaseReleased() => Interlocked.Increment(ref _leaseGeneration);

    private void CancelIfCopying()
    {
        if (Snapshot().State != "copying") return;
        lock (_gate) _cancellation.Cancel();
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
            if (!_proxyOnly && !IsSourceCopyValid(TargetPath, _sourceLength))
            {
                await CopyFileAsync(SourcePath, _sourceCopyPath, () =>
                {
                    lock (_gate)
                    {
                        _cancellation.Token.ThrowIfCancellationRequested();
                        File.Move(_sourceCopyPath, TargetPath, true);
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }

            if (_proxyOnly && CachedProxyPath == null)
                throw new InvalidOperationException("A completed edit proxy is required for this workspace.");

            if (CachedProxyPath != null && !StudioProxyJob.IsComplete(ProxyPath))
            {
                try
                {
                    await CopyFileAsync(CachedProxyPath, _proxyCopyPath, () =>
                    {
                        lock (_gate)
                        {
                            _cancellation.Token.ThrowIfCancellationRequested();
                            File.Move(_proxyCopyPath, ProxyPath, true);
                            File.WriteAllText(StudioProxyJob.CompleteMarker(ProxyPath), StudioProxyPlan.CacheVersion);
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    TryDelete(_proxyCopyPath);
                    if (_proxyOnly) throw;
                }
            }

            lock (_gate)
            {
                _cancellation.Token.ThrowIfCancellationRequested();
                _copied = _length;
                _state = "ready";
            }
        }
        catch (OperationCanceledException)
        {
            TryDelete(_sourceCopyPath);
            TryDelete(_proxyCopyPath);
        }
        catch
        {
            TryDelete(_sourceCopyPath);
            TryDelete(_proxyCopyPath);
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

    private static string WorkspaceKey(string itemId, string sourcePath, long length, long modifiedTicks)
    {
        var identity = $"{itemId}\0{sourcePath}\0{length}\0{modifiedTicks}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static bool IsSourceCopyValid(string path, long expectedLength)
    {
        try { return File.Exists(path) && new FileInfo(path).Length == expectedLength; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private static bool IsWithinDirectory(string filePath, string directoryPath)
    {
        var prefix = directoryPath.EndsWith(Path.DirectorySeparatorChar)
            ? directoryPath
            : directoryPath + Path.DirectorySeparatorChar;
        return filePath.StartsWith(prefix, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
    }

    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
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
