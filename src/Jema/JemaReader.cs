namespace Jema;

/// <summary>A frame-range reader. The caller owns the seekable stream and serializes access.</summary>
public sealed class JemaReader : IDisposable
{
    private readonly Stream source;
    private readonly Container container;
    private BlockCodec? codec;
    private byte[]? buffer;
    private bool disposed;

    public JemaReader(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("Input must be readable and seekable.", nameof(source));
        this.source = source;
        container = Container.Read(source);
    }

    public PcmLayout Layout => container.Layout;
    public long Frames => container.Frames;
    public int BlockFrames => container.BlockFrames;
    public long AudioBytes => container.AudioEnd;

    /// <summary>Writes complete interleaved frames into a buffer of exactly the requested byte length.</summary>
    public DecodeWork ReadFrames(long start, Span<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int bytesPerFrame = Layout.BytesPerFrame;
        if (pcm.Length % bytesPerFrame != 0)
            throw new ArgumentException("Destination must contain whole frames.", nameof(pcm));
        if (start < 0 || start > Frames || pcm.Length / bytesPerFrame > Frames - start)
            throw new ArgumentOutOfRangeException(nameof(start), "Range exceeds the audio timeline.");
        if (pcm.IsEmpty)
            return default;

        codec ??= new BlockCodec(Layout, container.BlockBytes);
        long readBytes = 0;
        long decodedBytes = 0;
        int blocks = 0;
        long block = start / BlockFrames;
        int offset = (int)(start % BlockFrames) * bytesPerFrame;
        while (!pcm.IsEmpty)
        {
            var entry = container.ReadEntry(source, block);
            readBytes += IndexEntry.Size;
            if (entry.Mode == BlockCodec.Reference)
            {
                var anchor = container.ReadEntry(source, entry.Position);
                if (anchor.Mode == BlockCodec.Reference || anchor.PcmCrc != entry.PcmCrc)
                    throw new InvalidDataException("A reference must name a stored block with matching checksum.");
                entry = anchor;
                readBytes += IndexEntry.Size;
            }

            int blockBytes = container.BytesInBlock(block);
            int count = Math.Min(pcm.Length, blockBytes - offset);
            if (offset == 0 && count == blockBytes)
            {
                codec.Decode(source, entry, pcm[..count]);
            }
            else
            {
                buffer ??= new byte[container.BlockBytes];
                var decoded = buffer.AsSpan(0, blockBytes);
                codec.Decode(source, entry, decoded);
                decoded.Slice(offset, count).CopyTo(pcm);
            }
            readBytes += entry.Length;
            decodedBytes += blockBytes;
            blocks++;
            pcm = pcm[count..];
            block++;
            offset = 0;
        }
        return new DecodeWork(readBytes, decodedBytes, blocks);
    }

    public JemaMetadata ReadMetadata()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return MetadataTrailer.Read(source, container.AudioEnd);
    }

    /// <summary>Copies the encoded audio and index verbatim and replaces both optional payloads.</summary>
    public void CopyWithMetadata(Stream destination, JemaMetadata? metadata)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("Metadata replacement requires a separate destination.", nameof(destination));
        JemaEncoder.RequireEmptyOutput(destination);
        MetadataTrailer.Validate(metadata);
        source.Position = 0;
        destination.Position = 0;
        var copy = new byte[128 * 1024];
        long left = container.AudioEnd;
        while (left > 0)
        {
            var segment = copy.AsSpan(0, (int)Math.Min(left, copy.Length));
            source.ReadExactly(segment);
            destination.Write(segment);
            left -= segment.Length;
        }
        MetadataTrailer.Write(destination, metadata);
    }

    public void Dispose()
    {
        codec?.Dispose();
        codec = null;
        buffer = null;
        disposed = true;
    }
}
