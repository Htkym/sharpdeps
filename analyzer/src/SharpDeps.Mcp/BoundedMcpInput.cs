namespace SharpDeps.Mcp;

using System.Text;

/// <summary>Bounds each UTF-8 stdio message before the SDK allocates a complete JSON line.</summary>
public sealed class BoundedMcpInput(Stream inner) : Stream
{
    public const int MaximumMessageBytes = 64 * 1024;
    private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
    private int lineBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Check(buffer.AsSpan(offset, read), read == 0 && count != 0);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var read = await inner.ReadAsync(buffer, token);
        Check(buffer.Span[..read], read == 0 && !buffer.IsEmpty);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    private void Check(ReadOnlySpan<byte> bytes, bool eof)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n') lineBytes = 0;
            else if (++lineBytes > MaximumMessageBytes) throw new IOException("MCP_INPUT_BUDGET_EXCEEDED");
        }
        try
        {
            Span<char> characters = stackalloc char[256];
            bool completed;
            do
            {
                decoder.Convert(bytes, characters, eof, out var used, out _, out completed);
                bytes = bytes[used..];
            } while (!completed);
        }
        catch (DecoderFallbackException) { throw new IOException("MCP_INPUT_ENCODING_INVALID"); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
