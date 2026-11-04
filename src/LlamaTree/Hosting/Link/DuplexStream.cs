namespace LlamaTree.Hosting.Link;

/// <summary>
/// Two one-way streams as one: reads from <paramref name="read"/>, writes to <paramref name="write"/> (a program's
/// output and input, say). Disposing it disposes both, then runs <paramref name="onDispose"/>.
/// </summary>
internal sealed class DuplexStream(Stream read, Stream write, Action? onDispose = null) : Stream
{
    private int disposed;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => read.ReadAsync(buffer, offset, count, ct);

    public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => write.WriteAsync(buffer, offset, count, ct);

    public override void Flush() => write.Flush();

    public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
        {
            try { write.Dispose(); } catch (Exception) { }
            try { read.Dispose(); } catch (Exception) { }
            onDispose?.Invoke();
        }
        base.Dispose(disposing);
    }
}
