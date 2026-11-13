namespace OpenLightFX.Emby.Studio;

/// <summary>
/// Reads a proxy file while ffmpeg is still appending fragments.
/// CanSeek is false and Length is unsupported so the host does not advertise a
/// finished Content-Length. Read blocks until new bytes exist, and returns 0
/// only after the job has left <c>building</c> and every written byte is consumed.
/// </summary>
public sealed class StudioGrowingStream : Stream
{
    private readonly StudioProxyJob _job;
    private readonly long _start;
    private FileStream? _file;
    private long _sent;

    public StudioGrowingStream(StudioProxyJob job, long start)
    {
        _job = job;
        _start = start < 0 ? 0 : start;
        _sent = _start;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var length = CurrentLength();
            if (length > _sent)
            {
                var file = Open();
                // A previous read can sit at EOF. Seeking again is what makes appended
                // fragments visible to this handle.
                if (file.Position != _sent)
                    file.Seek(_sent, SeekOrigin.Begin);

                var want = (int)Math.Min(buffer.Length, length - _sent);
                var read = await file.ReadAsync(buffer[..want], cancellationToken).ConfigureAwait(false);
                if (read > 0)
                {
                    _sent += read;
                    return read;
                }
            }

            if (_job.State is not ("building" or "missing") && _sent >= CurrentLength())
                return 0;

            try
            {
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }

        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _file?.Dispose();
        base.Dispose(disposing);
    }

    private FileStream Open() => _file ??= new FileStream(
        _job.OutputPath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite,
        65536,
        FileOptions.Asynchronous);

    private long CurrentLength()
    {
        try
        {
            return File.Exists(_job.OutputPath) ? new FileInfo(_job.OutputPath).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
