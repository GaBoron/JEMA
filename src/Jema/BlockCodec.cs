using System.Buffers.Binary;
using System.IO.Hashing;
using ZstdSharp;

namespace Jema;

internal sealed class BlockCodec(PcmLayout layout, int blockBytes, int? compressionLevel = null) : IDisposable
{
    public const byte Raw = 0;
    public const byte Zstd = 1;
    public const byte Reference = 255;
    private const byte FirstTransform = 2;
    private const byte FirstFixedPoint = 14;
    private readonly SampleTransform transform = new(layout, blockBytes);
    private readonly byte[] shuffled = new byte[blockBytes];
    private readonly byte[] encoded = new byte[blockBytes];
    private readonly Compressor? compressor = compressionLevel.HasValue ? new(compressionLevel.Value) : null;
    private readonly byte[]? candidate = compressionLevel.HasValue
        ? new byte[Compressor.GetCompressBound(blockBytes)]
        : null;
    private readonly Decompressor decompressor = new();
    private FixedPointTransform? fixedPoint;
    private byte[]? fixedCandidate;

    public static bool IsStoredMode(byte mode) => mode <= FirstFixedPoint + 1;

    public (byte Mode, int Length) Encode(ReadOnlySpan<byte> pcm, Stream destination, EncoderOptions options)
    {
        var scratch = candidate.AsSpan();
        int size = compressor!.Wrap(pcm, scratch);
        byte bestMode = size < pcm.Length ? Zstd : Raw;
        int bestSize = Math.Min(size, pcm.Length);
        if (bestMode == Zstd)
            scratch[..size].CopyTo(encoded);

        Predictor last = options.CrossTrackSignPrediction && layout.ChannelsPerTrack.Length > 1
            ? Predictor.TrackSign
            : Predictor.ChannelSign;
        for (Predictor predictor = Predictor.Word; predictor <= last; predictor++)
        {
            if (predictor == Predictor.ChannelSign && layout.ChannelsPerTrack.Length == layout.Channels)
                continue;

            transform.Predict(pcm, predictor);
            for (Shuffle shuffle = Shuffle.Byte; shuffle <= Shuffle.Bit; shuffle++)
            {
                var data = shuffled.AsSpan(0, pcm.Length);
                transform.ShuffleTo(data, shuffle);
                size = compressor.Wrap(data, scratch);
                if (size < bestSize)
                {
                    bestSize = size;
                    bestMode = (byte)(FirstTransform + 2 * (int)predictor + (int)shuffle);
                    scratch[..size].CopyTo(encoded);
                }
            }
        }

        if (options.FixedPointPrediction)
        {
            (byte mode, int length) = EncodeFixed(pcm, destination, bestSize);
            if (length > 0)
                return (mode, length);
        }
        destination.Write(bestMode == Raw ? pcm : encoded.AsSpan(0, bestSize));
        return (bestMode, bestSize);
    }

    private (byte Mode, int Length) EncodeFixed(ReadOnlySpan<byte> pcm, Stream destination, int limit)
    {
        fixedPoint ??= new FixedPointTransform(layout, blockBytes);
        fixedCandidate ??= new byte[candidate!.Length];
        int sideBytes = fixedPoint.Predict(pcm, transform.PredictedWords(pcm.Length / 4));
        var core = shuffled.AsSpan(0, pcm.Length);
        transform.ShuffleTo(core, Shuffle.Bit);
        int coreBytes = compressor!.Wrap(core, fixedCandidate);
        int prefixBytes = sizeof(int) + layout.Channels;
        byte mode = FirstFixedPoint + 1;
        transform.ShuffleTo(core, Shuffle.Byte);
        int byteShuffled = compressor.Wrap(core, candidate);
        if (byteShuffled < coreBytes)
        {
            candidate.AsSpan(0, byteShuffled).CopyTo(fixedCandidate);
            coreBytes = byteShuffled;
            mode = FirstFixedPoint;
        }

        if (prefixBytes + coreBytes >= limit)
            return default;
        var side = fixedPoint.Side[..sideBytes];
        int compressed = compressor.Wrap(side, candidate);
        int size = prefixBytes + coreBytes + Math.Min(compressed, sideBytes);
        if (size >= limit)
            return default;

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, coreBytes);
        destination.Write(length);
        destination.Write(fixedPoint.Exponents);
        destination.Write(fixedCandidate.AsSpan(0, coreBytes));
        destination.Write(compressed < sideBytes ? candidate.AsSpan(0, compressed) : side);
        return (mode, size);
    }

    public void Decode(Stream source, IndexEntry entry, Span<byte> pcm)
    {
        source.Position = entry.Position;
        if (entry.Mode == Raw)
        {
            source.ReadExactly(pcm);
        }
        else
        {
            var compressed = encoded.AsSpan(0, entry.Length);
            source.ReadExactly(compressed);
            if (entry.Mode >= FirstFixedPoint)
            {
                DecodeFixed(compressed, pcm, (Shuffle)(entry.Mode - FirstFixedPoint));
            }
            else
            {
                var data = entry.Mode == Zstd ? pcm : shuffled.AsSpan(0, pcm.Length);
                Inflate(compressed, data);
                if (entry.Mode != Zstd)
                {
                    int recipe = entry.Mode - FirstTransform;
                    transform.Unshuffle(data, (Shuffle)(recipe % 2));
                    transform.Restore(pcm, (Predictor)(recipe / 2));
                }
            }
        }
        if (Crc32.HashToUInt32(pcm) != entry.PcmCrc)
            throw new InvalidDataException("PCM block checksum mismatch.");
    }

    private void DecodeFixed(ReadOnlySpan<byte> compressed, Span<byte> pcm, Shuffle shuffle)
    {
        fixedPoint ??= new FixedPointTransform(layout, blockBytes);
        int prefixBytes = sizeof(int) + layout.Channels;
        if (compressed.Length <= prefixBytes)
            throw new InvalidDataException("Truncated fixed-point payload prefix.");
        int coreBytes = BinaryPrimitives.ReadInt32LittleEndian(compressed);
        if (coreBytes <= 0 || coreBytes >= compressed.Length - prefixBytes)
            throw new InvalidDataException("Invalid compressed fixed-point core size.");
        compressed.Slice(sizeof(int), layout.Channels).CopyTo(fixedPoint.Exponents);
        var core = shuffled.AsSpan(0, pcm.Length);
        Inflate(compressed.Slice(prefixBytes, coreBytes), core);
        transform.Unshuffle(core, shuffle);
        var words = transform.PredictedWords(pcm.Length / 4);
        var (remainderBits, sideBytes) = fixedPoint.Unpredict(words, pcm.Length / layout.BytesPerFrame);
        var side = fixedPoint.Side[..sideBytes];
        var input = compressed[(prefixBytes + coreBytes)..];
        if (input.Length == side.Length)
            input.CopyTo(side);
        else if (input.Length < side.Length)
            Inflate(input, side);
        else
            throw new InvalidDataException("Invalid fixed-point side stream size.");
        fixedPoint.Restore(pcm, words, remainderBits);
    }

    private void Inflate(ReadOnlySpan<byte> compressed, Span<byte> output)
    {
        try
        {
            if (!decompressor.TryUnwrap(compressed, output, out int written) || written != output.Length)
                throw new InvalidDataException("Incorrect block decompressed size.");
        }
        catch (ZstdException error)
        {
            throw new InvalidDataException("Invalid Zstandard payload.", error);
        }
    }

    public void Dispose()
    {
        compressor?.Dispose();
        decompressor.Dispose();
    }
}
