using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenLightFX.Emby.Studio;

public sealed class StudioProxyJob
{
    private long _encodedThroughMs;
    private int _state; // 0 building, 1 ready, 2 failed

    public StudioProxyJob(string itemId, string outputPath, double fps, int width, int height)
    {
        ItemId = itemId;
        OutputPath = outputPath;
        Fps = fps <= 0 ? 24 : fps;
        Width = Even(width);
        Height = Even(height);
        KeyframeIntervalMs = StudioProxyPlan.IntervalMs(Fps);
    }

    public string ItemId { get; }
    public string OutputPath { get; }
    public double Fps { get; }
    public int Width { get; }
    public int Height { get; }
    public int KeyframeIntervalMs { get; }
    public long EncodedThroughMs => Interlocked.Read(ref _encodedThroughMs);

    public string State => _state switch
    {
        1 => "ready",
        2 => "failed",
        _ => "building"
    };

    public void MarkReady(long encodedThroughMs)
    {
        Interlocked.Exchange(ref _encodedThroughMs, encodedThroughMs);
        Volatile.Write(ref _state, 1);
    }

    public void NoteProgress(long encodedThroughMs)
    {
        if (encodedThroughMs < 0)
            return;
        long current;
        do
        {
            current = Interlocked.Read(ref _encodedThroughMs);
            if (encodedThroughMs <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref _encodedThroughMs, encodedThroughMs, current) != current);
    }

    public void MarkFailed() => Volatile.Write(ref _state, 2);

    public void Start(string ffmpegPath, IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = StudioProcess.StartInfo(ffmpegPath, arguments),
            EnableRaisingEvents = true
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null && StudioFfmpegProgress.TryReadEncodedMs(args.Data, out var ms))
                NoteProgress(ms);
        };
        process.Exited += (_, _) =>
        {
            try
            {
                if (process.ExitCode == 0 && File.Exists(OutputPath))
                    MarkReady(Math.Max(EncodedThroughMs, 0));
                else
                    MarkFailed();
            }
            catch
            {
                MarkFailed();
            }
            finally
            {
                process.Dispose();
            }
        };

        if (!process.Start())
        {
            MarkFailed();
            return;
        }

        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
    }

    private static int Even(int value)
    {
        if (value <= 0)
            return 0;
        return value - (value % 2);
    }
}

public static class StudioProxyCache
{
    private static readonly ConcurrentDictionary<string, StudioProxyJob> Jobs = new(StringComparer.OrdinalIgnoreCase);

    public static StudioProxyJob GetOrAdd(string itemId, Func<StudioProxyJob> create) =>
        Jobs.GetOrAdd(itemId, _ => create());

    public static bool TryGet(string itemId, out StudioProxyJob job) => Jobs.TryGetValue(itemId, out job!);
}

public static class StudioProcess
{
    public static ProcessStartInfo StartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        var bin = Path.GetDirectoryName(fileName);
        if (!string.IsNullOrEmpty(bin))
        {
            var lib = Path.GetFullPath(Path.Combine(bin, "..", "lib"));
            if (Directory.Exists(lib))
            {
                var current = info.Environment.TryGetValue("LD_LIBRARY_PATH", out var existing) ? existing : "";
                info.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(current) ? lib : lib + ":" + current;
            }
        }

        return info;
    }

    public static async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfo(fileName, arguments) };
        process.Start();
        var stdout = process.StandardOutput.BaseStream.ReadAllAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadAllAsync(this Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
