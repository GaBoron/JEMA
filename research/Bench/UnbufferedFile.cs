using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>Windows OS-cache bypass with sector-rounded reads for the storage benchmark.</summary>
internal sealed unsafe class UnbufferedFile : Stream
{
    private const int ChunkBytes = 4 * 1024 * 1024;
    private readonly SafeFileHandle handle;
    private readonly long length;
    private readonly int alignment;
    private byte* scratch;
    private long position;

    public UnbufferedFile(string path, int alignment)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unbuffered benchmarking requires Windows.");
        if (alignment is < 512 or > 65536 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        this.alignment = alignment;
        const uint genericRead = 0x80000000;
        const uint shareRead = 1;
        const uint openExisting = 3;
        const uint noBuffering = 0x20000000;
        handle = CreateFile(Path.GetFullPath(path), genericRead, shareRead, 0, openExisting, noBuffering, 0);
        if (handle.IsInvalid)
            throw new IOException("Could not open unbuffered input.", new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            length = RandomAccess.GetLength(handle);
            scratch = (byte*)NativeMemory.AlignedAlloc(ChunkBytes + (nuint)alignment, (nuint)alignment);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public long IssuedBytes { get; set; }
    public override bool CanRead => !handle.IsClosed;
    public override bool CanSeek => !handle.IsClosed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => position;
        set => position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        if (buffer.IsEmpty || position >= length)
            return 0;
        long aligned = position & -(long)alignment;
        int inset = (int)(position - aligned);
        int requested = (int)Math.Min(Math.Min(length - position, ChunkBytes), buffer.Length);
        int rounded = (requested + inset + alignment - 1) & -alignment;
        var sector = new Span<byte>(scratch, rounded);
        int read = RandomAccess.Read(handle, sector, aligned);
        IssuedBytes += rounded;
        int available = Math.Min(requested, Math.Max(0, read - inset));
        sector.Slice(inset, available).CopyTo(buffer);
        position += available;
        return available;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = checked(offset + (origin switch
        {
            SeekOrigin.Begin => 0,
            SeekOrigin.Current => position,
            SeekOrigin.End => length,
            _ => throw new ArgumentException("Unknown seek origin.", nameof(origin))
        }));
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            handle.Dispose();
        NativeMemory.AlignedFree(scratch);
        scratch = null;
        base.Dispose(disposing);
    }

    ~UnbufferedFile() => Dispose(false);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
}
